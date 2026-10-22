using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hWnd);

    // Held for the whole run: a second copy of the tool just exits instead of
    // stacking another full-screen overlay on top of the first.
    static Mutex instanceMutex;

    // DSH primary blue (#4176E6): selection frame, its handles and the magnifier
    // crosshair all use it, so the tool looks like part of the app.
    public static readonly Color Accent = Color.FromArgb(65, 118, 230);

    // Light/dark card palette. Windows' app-theme switch is the default signal;
    // --dark / --light override it so the host (or a test) can force either one.
    public static bool DarkMode;

    // Diagnostics stay in the build but are off unless DSH_SNIP_DEBUG=1: a shipped plugin
    // must not write a log file on every capture.
    static readonly bool DebugLog = System.Environment.GetEnvironmentVariable("DSH_SNIP_DEBUG") == "1";

    public static void TLog(string s)
    {
        if (!DebugLog) return;
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-text.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n");
        }
        catch (Exception) { }
    }

    static bool SystemUsesDarkApps()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                if (value is int) return (int)value == 0;
            }
        }
        catch (Exception) { }
        return false;
    }

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // A distributed plugin must never pop the CLR crash dialog: swallow, log, keep going.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        if (args.Length >= 2 && args[0] == "--capture")
        {
            var vs = SystemInformation.VirtualScreen;
            using (var bmp = new Bitmap(vs.Width, vs.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size);
                bmp.Save(args[1], ImageFormat.Png);
            }
            return;
        }

        bool createdNew;
        instanceMutex = new Mutex(true, @"Local\dsh-screenshot-snip", out createdNew);
        if (!createdNew)
        {
            // Focus the overlay that is already up so the second click feels like it
            // did something (and so Esc lands on the right window).
            var existing = FindWindow(null, Program.T("截图", "Screenshot"));
            if (existing == IntPtr.Zero) existing = FindWindow(null, "Screenshot");
            if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); return; }
            // No overlay: a warm process is waiting for work, so pass the request on to it.
            WakeWarmOwner();
            return;
        }

        // TEMPORARY: which exe is actually running (build time says whether an old
        // copy is still up, which is the usual reason a fix "does nothing").
        TLog("start exe=" + Application.ExecutablePath + " built=" +
            System.IO.File.GetLastWriteTime(Application.ExecutablePath).ToString("HH:mm:ss"));
        DarkMode = SystemUsesDarkApps();
        bool hide = false, serve = false, showNow = false;
        int parentPid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--hide") hide = true;
            else if (args[i] == "--dark") DarkMode = true;
            else if (args[i] == "--light") DarkMode = false;
            else if (args[i] == "--serve") serve = true;
            else if (args[i] == "--show") showNow = true;
            else if (args[i] == "--parent" && i + 1 < args.Length) int.TryParse(args[i + 1], out parentPid);
            else if (args[i] == "--lang" && i + 1 < args.Length) LangZh = args[i + 1].ToLowerInvariant().StartsWith("zh");
        }
        AppDomain.CurrentDomain.UnhandledException += (s2, ev2) =>
        {
            try { LogCrash("EX " + ev2.ExceptionObject); } catch (Exception) { }
        };
        Application.ThreadException += (s2, ev2) =>
        {
            try { LogCrash("TEX " + ev2.Exception); } catch (Exception) { }
        };
        if (!serve)
        {
            Application.Run(new SnipForm(hide));
            return;
        }

        // Warm mode: first call runs a session, then the process waits for the next one so
        // the plugin's button feels instant instead of paying the startup every time.
        StartPipeServer(parentPid);
        if (showNow) RunSession(hide);
        else WarmUpStatics();
        TrimIdle();      // waiting around must not cost 30 MB of working set
        long idleDeadline = Environment.TickCount + 180000;
        while (true)
        {
            if (!RequestEvent.WaitOne(2000))
            {
                if (parentPid != 0 && !ParentAlive(parentPid)) break;
                if (Environment.TickCount >= idleDeadline) break;
                continue;
            }
            string request = PendingRequest ?? "show";
            PendingRequest = null;
            idleDeadline = Environment.TickCount + 180000;
            Program.TLog("serve: wake [" + request + "]");
            if (request.Contains("theme=dark")) DarkMode = true;
            else if (request.Contains("theme=light")) DarkMode = false;
            // The host passes the language DSH itself is running in, so the capture UI
            // always matches the application instead of the OS.
            if (request.Contains("lang=zh")) LangZh = true;
            else if (request.Contains("lang=en")) LangZh = false;
            RunSession(request.Contains("hide=1"));
            Program.TLog("serve: session ended");
            TrimIdle();
        }
    }

    // Each capture gets a fresh STA thread with its own WinForms message loop. Reusing
    // the thread across sessions left a stale thread context behind, and the next
    // session died creating its window handle:
    //   ObjectDisposedException: cannot access a disposed object: "SnipForm"
    //   at System.Windows.Forms.Control.CreateHandle()
    static void RunSession(bool hide)
    {
        Exception failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using (var form = new SnipForm(hide))
                {
                    Application.Run(form);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure != null) Program.LogCrash("serve session: " + failure);
    }

    // Pipe the plugin talks to: writes "show hide=1" (or "show") for each capture.
    public const string PipeName = "dsh-snip-show";

    // Chinese by default, English elsewhere; --lang en/zh overrides for testing.
    public static bool LangZh = System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName == "zh";

    public static string T(string zh, string en) { return LangZh ? zh : en; }

    public static void LogCrash(string text)
    {
        if (!DebugLog) return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-snip-error.log"),
                DateTime.Now.ToString("HH:mm:ss") + " " + text + "\r\n");
        }
        catch (Exception) { }
    }

    // Waits up to timeoutMs for a request. Returns null when the parent is gone or the
    // process has been idle too long (so a warm process never becomes a permanent resident).
    // One thread owns the pipe for the whole run and re-arms immediately after every
    // message, so a click that lands while a capture is still on screen is queued instead
    // of being lost (that gap is why presses used to do nothing).
    static volatile string PendingRequest;
    static readonly System.Threading.AutoResetEvent RequestEvent = new System.Threading.AutoResetEvent(false);
    static volatile bool ParentGone;

    static void StartPipeServer(int parentPid)
    {
        var thread = new System.Threading.Thread(() =>
        {
            while (!ParentGone)
            {
                try
                {
                    using (var server = new System.IO.Pipes.NamedPipeServerStream(
                        PipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None))
                    {
                        server.WaitForConnection();
                        string line = null;
                        using (var reader = new System.IO.StreamReader(server, System.Text.Encoding.ASCII, false, 1024, true))
                            line = reader.ReadLine();
                        if (line != null && line.StartsWith("show"))
                        {
                            // Acknowledge before queueing: the host retries when no "ok"
                            // comes back, which is how a lost press gets recovered.
                            try
                            {
                                var ack = System.Text.Encoding.ASCII.GetBytes("ok\n");
                                server.Write(ack, 0, ack.Length);
                                server.Flush();
                            }
                            catch (Exception) { }
                            PendingRequest = line;
                            RequestEvent.Set();
                        }
                    }
                }
                catch (Exception) { System.Threading.Thread.Sleep(50); }
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    // Between sessions the process should look idle in Task Manager: collect, then hand
    // the unused pages back to the OS.
    static void TrimIdle()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using (var p = Process.GetCurrentProcess()) Program.EmptyWorkingSet(p.Handle);
        }
        catch (Exception) { }
    }

    // Hand a request to the running instance.
    static bool WakeWarmOwner()
    {
        bool acked = false;
        try
        {
            using (var client = new System.IO.Pipes.NamedPipeClientStream(".", PipeName,
                System.IO.Pipes.PipeDirection.Out))
            {
                client.Connect(400);
                using (var writer = new System.IO.StreamWriter(client))
                {
                    writer.WriteLine("show");
                    writer.Flush();
                }
                // Wait for the ack: a listener that never answers is a wedged one.
                var read = client.ReadAsync(new byte[8], 0, 8);
                acked = read.Wait(600) && read.Result > 0;
            }
        }
        catch (Exception) { return false; }
        return acked;
    }

    // Pay the JIT for the session paths while the process is idle: constructing the form,
    // the toolbar and the text measuring path costs ~0.5s the first time, and that was
    // landing on the very first click.
    static void WarmUpStatics()
    {
        try
        {
            using (var form = new SnipForm(false))
            {
                // Never look like a live overlay: the "already running" path finds overlays
                // by their window title.
                form.Text = "dsh-snip-warmup";
                var probe = new Anno { Tool = "text", Text = "啊", Width = 21, BoxW = 120 };
                probe.Points.Add(new Point(10, 10));
                form.MeasureTextFor(probe);
                using (var bmp = new Bitmap(64, 64))
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var bar = new ToolBarForm())
                    {
                        bar.Add("pen", "画笔", null, Icons.Pen);
                        bar.Add("text", "文本", null, Icons.Text);
                        bar.Strip.PaintContent(g);
                        Icons.UndoEnabled = true;
                        bar.Strip.PaintContent(g);
                    }
                }
            }
        }
        catch (Exception) { }
    }

    static bool ParentAlive(int pid)
    {
        try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
        catch (Exception) { return false; }
    }
}

public class Anno
{
    public string Tool;
    public List<Point> Points = new List<Point>();
    public string Text;
    public int Number;
    public int Width;      // stroke width the annotation was drawn with
    public Color Color;    // and its colour
    public bool Filled;    // boxes drawn solid instead of stroked
    public int BoxW;      // text: wrap width used when it was typed
    // Cached TextBounds result: the measure needs a Graphics plus a Font and used to run
    // on every paint and every hit test.
    internal string MeasText;
    internal int MeasPx, MeasBoxW, MeasW, MeasH;
}

public class SnipForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);

    Bitmap screen;
    Point origin;
    Rectangle sel;
    bool hasSel;
    string tool = "select";
    readonly List<Anno> annos = new List<Anno>();
    Anno draft;
    int dragMode; // 1 new, 2 move, 3 resize, 4 pending click
    Rectangle moveBox;    // bounds of the annotation being dragged (containment clamp)
    int handle = -1;
    Point dragStart;
    Rectangle selStart;
    TextBox textBox;
    SnipTextBox sb;
    ToolBarForm bar;
    Label hint;
    bool closing;   // teardown started: late callbacks from other windows must do nothing
    bool hideDsh;
    IntPtr dshWnd = IntPtr.Zero;
    Point lastMouse;
    Rectangle lastLoupe;
    LoupeForm loupe;
    Point lastScreen;
    System.Windows.Forms.Timer cursorTimer;
    Rectangle hoverRect;
    bool hasHover;
    Point lastProbe;
    DateTime lastProbeAt = DateTime.MinValue;

    // Toolbar palette, resolved once from the light/dark mode.
    Color barBack, barFore, barHover, barOn;

    void InitTheme()
    {
        bool dark = Program.DarkMode;
        barBack = dark ? Color.FromArgb(38, 38, 38) : Color.FromArgb(248, 248, 248);
        barFore = dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(45, 45, 45);
        barHover = dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(232, 232, 232);
        barOn = dark ? Color.FromArgb(44, 76, 138) : Color.FromArgb(215, 232, 250);
    }

    public SnipForm(bool hide)
    {
        hideDsh = hide;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        // Direct painting: the overlay is composed with GDI BitBlt straight onto the
        // window DC. Double buffering would push every frame through a 32bpp buffer
        // and triple the cost.
        DoubleBuffered = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Cursor = Cursors.Default;
        Text = Program.T("截图", "Screenshot");
    }

    // Clicking outside the editor has to end text editing even when the input method
    // swallows that click; a low-level hook sees it before anything else does.
    delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct HOOKPOINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT
    {
        public HOOKPOINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo;
    }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc cb, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    IntPtr mouseHook;
    MouseHookProc mouseHookProc;
    // Set when the hook ends editing for a click: that same click must be eaten.
    Point? swallowClick;
    DateTime swallowAt;

    void InstallMouseHook()
    {
        if (mouseHook != IntPtr.Zero || mouseHookProc != null) return;
        mouseHookProc = MouseHookCallback;
        mouseHook = SetWindowsHookEx(14, mouseHookProc, IntPtr.Zero, 0);   // WH_MOUSE_LL
    }

    void UninstallMouseHook()
    {
        if (mouseHook != IntPtr.Zero) { try { UnhookWindowsHookEx(mouseHook); } catch (Exception) { } mouseHook = IntPtr.Zero; }
        mouseHookProc = null;   // otherwise the next overlay never installs its own hook
    }

    IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (IsDisposed || closing) return IntPtr.Zero;   // a queued hook call can outlive the form
        // While the overlay is up the right button cancels the capture. The desktop used to
        // steal it (opening its context menu), so it is swallowed here, before delivery.
        if (nCode >= 0 && (wParam == (IntPtr)0x0204 || wParam == (IntPtr)0x0205) && Visible && !closing)
        {
            // Swallow both halves and cancel on the release, so neither button event ever
            // reaches the desktop.
            if (wParam == (IntPtr)0x0205)
            {
                try { BeginInvoke((MethodInvoker)(() => { if (!closing && Visible) Close(); })); }
                catch (Exception) { }
            }
            return (IntPtr)1;
        }
        if (nCode >= 0 && wParam == (IntPtr)0x0201 && editor != null)   // WM_LBUTTONDOWN
        {
            try
            {
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                var pt = new Point(info.pt.X, info.pt.Y);
                if (!editor.Bounds.Contains(pt))
                {
                    swallowClick = pt;
                    swallowAt = DateTime.Now;
                    var ed = editor;
                    // Deferred: the hook must return fast and the click may still be queued.
                    BeginInvoke((MethodInvoker)(() => { if (editor == ed) ed.CommitText(); }));
                }
            }
            catch (Exception) { }
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    void FindDsh()
    {
        foreach (var p in Process.GetProcessesByName("DeepSeek Harness"))
        {
            if (p.MainWindowHandle != IntPtr.Zero) { dshWnd = p.MainWindowHandle; return; }
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        InitTheme();
        var vs = SystemInformation.VirtualScreen;
        origin = vs.Location;
        Bounds = vs;
        if (hideDsh)
        {
            FindDsh();
            if (dshWnd != IntPtr.Zero)
            {
                Program.ShowWindow(dshWnd, 6);
                // Wait for the minimise to land instead of a flat 420ms: the window is
                // iconic almost at once, the animation only needs a short grace period.
                for (int i = 0; i < 15 && !Program.IsIconic(dshWnd); i++) Thread.Sleep(10);
                Thread.Sleep(200);   // DWM fades the window out; too short and it lands in the shot
            }
        }
        screen = new Bitmap(vs.Width, vs.Height);
        using (var g = Graphics.FromImage(screen))
            g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size);
        // Deliberately NOT restored here: while snipping, DSH stays minimised so it is
        // neither in the picture nor under the cursor. It comes back when the tool ends.

        // InitBlit builds the dimmed copy; building it here as well wasted a full-screen
        // bitmap and a 2560x1600 blend on every single startup.
        InitGdi();
        InitBlit();

        bar = new ToolBarForm();
        bar.Add("pen", Program.T("画笔", "Pen"), () => ToggleTool("pen"), Icons.Pen);
        bar.Add("line", Program.T("线条", "Line"), () => ToggleTool("line"), Icons.Line);
        bar.Add("arrow", Program.T("箭头", "Arrow"), () => ToggleTool("arrow"), Icons.Arrow);
        bar.Add("rect", Program.T("矩形", "Rectangle"), () => ToggleTool("rect"), Icons.Rect);
        bar.Add("ellipse", Program.T("椭圆", "Ellipse"), () => ToggleTool("ellipse"), Icons.Ellipse);
        bar.Add("text", Program.T("文本", "Text"), () => ToggleTool("text"), Icons.Text);
        bar.Add("undo", Program.T("撤销", "Undo"), UndoLast, Icons.Undo);
        bar.Add("save", Program.T("下载", "Save"), SaveToFile, Icons.Save);
        bar.Add("close", Program.T("退出", "Cancel"), () => Close(), Icons.Close);
        bar.Add("done", Program.T("完成", "Done"), () => CopyAndClose(), Icons.Check);
        bar.Strip.PenWidth = 5;
        bar.Strip.PenColor = Color.FromArgb(255, 59, 48);
        bar.Strip.PenChanged = SaveInk;

        hint = new Label
        {
            AutoSize = true,
            BackColor = Color.FromArgb(160, 0, 0, 0),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9F),
            Text = "拖动框选，或点击高亮的窗口 · Esc 取消  (v52)",
            Padding = new Padding(10, 5, 10, 5),
        };
        Controls.Add(hint);
        // No hint strip across the top: the toolbar is self-explanatory and the band
        // sat right on the screenshot.
        hint.Visible = false;
        PositionChrome();
        // Run the two hot paths once while nothing is on screen yet: otherwise the very
        // first mouse move (13ms) and the first full paint (12ms) landed together and
        // the pointer visibly hitched the moment the overlay appeared.
        try
        {
            using (var warm = new Bitmap(8, 8))
            using (var wg = Graphics.FromImage(warm))
                PaintCore(wg, new PaintEventArgs(wg, new Rectangle(0, 0, 8, 8)));
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0,
                ClientSize.Width / 2, ClientSize.Height / 2, 0));
        }
        catch (Exception) { }

        sb = new SnipTextBox
        {
            Visible = false,
            Multiline = true,
            WordWrap = true,
            Font = new Font("Microsoft YaHei UI", 11F),
            BorderStyle = BorderStyle.None,
            BackColor = Program.DarkMode ? Color.FromArgb(30, 30, 30) : Color.White,
            ForeColor = Program.DarkMode ? Color.FromArgb(235, 235, 235) : Color.Black,
        };
        textBox = sb;

        // Grows to the right while it fits, then wraps at the frame edge and grows down:
        // exactly the two behaviours the user asked for, in that order.
        textBox.TextChanged += (s2, e2) =>
        {
            try
            {
                string txt = textBox.Text.Length == 0 ? " " : textBox.Text;
                int need = TextRenderer.MeasureText(txt, textBox.Font).Width + 22;
                int avail = hasSel ? Math.Max(140, sel.Right - 8 - textBox.Left)
                                   : Math.Max(140, ClientSize.Width - 16 - textBox.Left);
                int wantW = Math.Min(need, avail);
                if (textBox.Width != wantW) textBox.Width = wantW;

                var sz = TextRenderer.MeasureText(txt, textBox.Font,
                    new Size(Math.Max(80, wantW - 10), 4096),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                int wantH = Math.Max(textBox.Font.Height + 10, sz.Height + 8);
                if (textBox.Height != wantH) textBox.Height = wantH;
                PlaceImeWindow();
                Invalidate();
            }
            catch (Exception) { }
        };

        sb.ImeStarted = () => PlaceImeWindow();

        textBox.KeyDown += (s, ev) =>
        {
            if (ev.KeyCode == Keys.Enter) { CommitText(); ev.SuppressKeyPress = true; }
            else if (ev.KeyCode == Keys.Escape) { textBox.Visible = false; }
        };
        Controls.Add(textBox);
        textBox.BringToFront();

        cursorTimer = new System.Windows.Forms.Timer { Interval = 16 };
        cursorTimer.Tick += (s, ev) =>
        {
            var sp = Cursor.Position;
            if (sp != lastScreen)
            {
                lastScreen = sp;
                if (loupe != null) loupe.SetPoint(sp);
            }
            var pos = PointToClient(sp);
            if (pos != lastMouse) lastMouse = pos;
        };
        // Not started here: the magnifier is driven from OnMouseMove, so an idle overlay
        // must not wake the process 60 times a second.
        InstallMouseHook();
    }

    // The toolbar stays visible while the frame is edited. Hiding it left stale
    // pixels on this system (the overlay's repaint did not clear the area), so the
    // hide-on-drag behaviour waits for the toolbar to be painted inside the overlay.
    void HideBar()
    {
        if (bar == null || !bar.Visible) return;
        bar.Hide();
        // The toolbar is a separate window: once it is gone the overlay has to repaint
        // the strip it covered, so ask for a full frame instead of the dirty strips.
        pendingDirty.Clear();
        Invalidate();
    }
    // One undo step, shared by Ctrl+Z and the toolbar button.
    void UndoLast()
    {
        if (closing || IsDisposed) return;
        if (undoSteps.Count > 0)
        {
            var step = undoSteps[undoSteps.Count - 1];
            undoSteps.RemoveAt(undoSteps.Count - 1);
            if (step.Points == null)
            {
                annos.Remove(step.Anno);
                if (activeAnno == step.Anno) { activeAnno = null; activeEnd = -1; }
            }
            else
            {
                step.Anno.Points.Clear();
                step.Anno.Points.AddRange(step.Points);
            }
        }
        else if (annos.Count > 0)
        {
            annos.RemoveAt(annos.Count - 1);
            activeAnno = null;
            activeEnd = -1;
        }
        Invalidate();
        if (bar != null) bar.Strip.Invalidate();
    }

    void ShowBar()
    {
        if (closing || IsDisposed || bar == null || bar.IsDisposed) return;
        if (!bar.Visible)
        {
            // Position before showing: showing it first made the bar appear at its default
            // bounds (bottom of the screen) for one frame and then jump to its real spot.
            hasSel = true;
            sel = new Rectangle(sel.X, sel.Y, Math.Max(2, sel.Width), Math.Max(2, sel.Height));
            PositionChrome();
            bar.Show(this);
        }
        if (bar != null) { bar.Strip.CanUndo = () => !IsDisposed && (undoSteps.Count > 0 || annos.Count > 0); bar.Strip.Invalidate(); }
        PositionChrome();
        bar.BringToFront();
        bar.RefreshRegion();
    }

    // The line just drawn stays selected and shows draggable end handles.
    Anno activeAnno;
    int activeEnd;

    static float Dist(Point a, Point b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    static bool SamePoints(List<Point> a, List<Point> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static float DistToSegment(Point p, Point a, Point b)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float len2 = vx * vx + vy * vy;
        if (len2 < 0.001f) return Dist(p, a);
        float t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return Dist(p, new Point((int)(a.X + vx * t), (int)(a.Y + vy * t)));
    }

    Point[] bodyStart;   // endpoints when a body drag began

    // Undo history: one entry per user action. Points == null means "the annotation was
    // created" (undo removes it); otherwise it holds the shape's points before a drag, so
    // undo restores the previous position/size.
    class UndoStep
    {
        public Anno Anno;
        public List<Point> Points;
    }
    readonly List<UndoStep> undoSteps = new List<UndoStep>();
    List<Point> dragUndoPoints;   // snapshot taken when a drag starts
    Rectangle shapeStart; // box at the moment a handle drag began

    // Annotations that can be selected and reshaped by their two ends.
    static bool Adjustable(Anno a)
    {
        return a != null && a.Points.Count >= 2 &&
               (a.Tool == "line" || a.Tool == "arrow" || a.Tool == "rect" || a.Tool == "ellipse");
    }

    // Four corners then four edge midpoints, matching the frame's handle layout.
    Point[] HandlesOf(Anno a)
    {
        if (!Adjustable(a)) return new Point[0];
        if (a.Tool != "rect" && a.Tool != "ellipse") return new[] { a.Points[0], a.Points[a.Points.Count - 1] };
        var r = Normalize(a.Points[0], a.Points[a.Points.Count - 1]);
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(r.Right, r.Top),
            new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom),
            new Point(cx, r.Top), new Point(r.Right, cy),
            new Point(cx, r.Bottom), new Point(r.Left, cy),
        };
    }

    // Which handle of the given annotation is under this point (-1 if none).
    int HandleAt(Anno a, Point p)
    {
        if (!Adjustable(a)) return -1;
        var hs = HandlesOf(a);
        for (int i = 0; i < hs.Length; i++) if (Dist(p, hs[i]) <= 12f) return i;
        return -1;
    }

    // The four system resize cursors, picked by what that handle actually changes.
    static Cursor HandleCursor(Anno a, int idx)
    {
        if (a.Tool == "rect" || a.Tool == "ellipse")
        {
            switch (idx)
            {
                case 0: case 2: return Cursors.SizeNWSE;   // corners on one diagonal
                case 1: case 3: return Cursors.SizeNESW;   // corners on the other
                case 4: case 6: return Cursors.SizeNS;     // top / bottom edge
                default: return Cursors.SizeWE;            // left / right edge
            }
        }
        // Lines and arrows: pick the axis the end mostly travels along.
        var p0 = a.Points[0];
        var p1 = a.Points[a.Points.Count - 1];
        return Math.Abs(p1.X - p0.X) >= Math.Abs(p1.Y - p0.Y) ? Cursors.SizeNS : Cursors.SizeWE;
    }

    // The opposite edge stays where it was when the drag started; the dragged edge
    // follows the cursor and may cross it, flipping the box. (Clamping it at the
    // opposite edge was why the right edge stopped dead against the left one.)
    void ReshapeRect(Anno a, int idx, Point p)
    {
        int l = shapeStart.Left, t = shapeStart.Top, rt = shapeStart.Right, b = shapeStart.Bottom;
        switch (idx)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: rt = p.X; t = p.Y; break;
            case 2: rt = p.X; b = p.Y; break;
            case 3: l = p.X; b = p.Y; break;
            case 4: t = p.Y; break;
            case 5: rt = p.X; break;
            case 6: b = p.Y; break;
            default: l = p.X; break;
        }
        a.Points.Clear();
        a.Points.Add(new Point(Math.Min(l, rt), Math.Min(t, b)));
        a.Points.Add(new Point(Math.Max(l, rt), Math.Max(t, b)));
    }

    // Box used only for the containment clamps. Text glyphs sit a little left of their
    // advance box, so the same visible gap needs a few extra pixels on the right.
    // A measured text box is taller than its glyphs and starts a little left of them, so
    // the same 6px margin looked different on each side. Tighten it to the ink alike.
    static Rectangle TextClampBox(Rectangle b)
    {
        // Measured against the glyph ink the left gap is ~20px while the others were ~7.
        // Keep the left as the reference and even the other three out to match it.
        b.Inflate(0, -4);
        b.Width += 13;
        return b;
    }

    Rectangle ClampBox()
    {
        var b = AnnotationBounds();
        // While a text is being typed it is not an annotation yet: without this the frame
        // slid right up to the editor's own edge, which looked like a tiny gap.
        if (editor != null && !editor.IsDisposed)
        {
            var er = new Rectangle(editor.Left - Left, editor.Top - Top, editor.Width, editor.Height);
            b = b.IsEmpty ? er : Rectangle.Union(b, er);
        }
        foreach (var a in annos)
            if (a.Tool == "text") return TextClampBox(b);
        return b;
    }

    Rectangle AnnoBounds(Anno a)
    {
        if (a == null) return Rectangle.Empty;
        if (a.Tool == "text") return TextBounds(a);
        if (a.Points.Count > 1) return Normalize(a.Points[0], a.Points[a.Points.Count - 1]);
        return Rectangle.Empty;
    }

    // Union of what the annotations occupy: the frame has to keep containing it.
    Rectangle AnnotationBounds()
    {
        Rectangle u = Rectangle.Empty;
        foreach (var a in annos)
        {
            Rectangle r;
            if (a.Tool == "text") r = TextBounds(a);
            else if (a.Points.Count > 1) r = Normalize(a.Points[0], a.Points[a.Points.Count - 1]);
            else continue;
            if (r.Width <= 0 || r.Height <= 0) continue;
            u = u.IsEmpty ? r : Rectangle.Union(u, r);
        }
        return u;
    }

    // A text under the pointer: hovering it shows the drag cursor and pressing it drags.
    Anno TextUnder(Point p)
    {
        for (int i = annos.Count - 1; i >= 0; i--)
        {
            var a = annos[i];
            if (a.Tool == "text" && OnInk(a, p, 8f)) return a;
        }
        return null;
    }

    // Is the point on the annotation's own ink? Boxes must be hit on their outline:
    // using the two stored corners treated the diagonal as the stroke, so clicking
    // anywhere inside a rectangle selected it.
    // Bounding box of a text annotation, measured with its own font.
    // Fonts are expensive to create and were built for every single measurement.
    readonly Dictionary<int, Font> measureFonts = new Dictionary<int, Font>();

    Font MeasureFont(int px)
    {
        Font f;
        if (!measureFonts.TryGetValue(px, out f)) { f = new Font("Microsoft YaHei UI", px); measureFonts[px] = f; }
        return f;
    }

    // Same work as TextBounds, exposed so the idle warm-up can pay its JIT cost.
    internal Rectangle MeasureTextFor(Anno a) { return TextBounds(a); }

    Rectangle TextBounds(Anno a)
    {
        if (a.Tool != "text" || a.Points.Count == 0) return Rectangle.Empty;
        int px = a.Width > 0 ? a.Width : 16;
        int wrapW = a.BoxW > 0 ? a.BoxW : 320;
        string cacheText = string.IsNullOrEmpty(a.Text) ? " " : a.Text;
        if (a.MeasText == cacheText && a.MeasPx == px && a.MeasBoxW == wrapW && a.MeasW > 0)
            return new Rectangle(a.Points[0].X - 2, a.Points[0].Y - 2, a.MeasW, a.MeasH);
        try
        {
            using (var g = CreateGraphics())
            {
                var f = MeasureFont(px);
                // Measure the characters themselves: MeasureString adds a trailing sliver
                // of its own, which showed up as a wider gap on the right than on the left.
                string txt = cacheText;
                var sf = new StringFormat(StringFormat.GenericTypographic);
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, txt.Length) });
                var regions = g.MeasureCharacterRanges(txt, f,
                    new RectangleF(0, 0, wrapW, 10000f), sf);
                var bb = regions[0].GetBounds(g);
                foreach (var rg in regions) bb = RectangleF.Union(bb, rg.GetBounds(g));
                a.MeasText = cacheText;
                a.MeasPx = px;
                a.MeasBoxW = wrapW;
                a.MeasW = (int)Math.Ceiling(bb.Right) + 4;
                a.MeasH = (int)Math.Ceiling(bb.Bottom) + 4;
                return new Rectangle(a.Points[0].X - 2, a.Points[0].Y - 2, a.MeasW, a.MeasH);
            }
        }
        catch (Exception) { return Rectangle.Empty; }
    }

    bool Selectable(Anno a)
    {
        return Adjustable(a) || (a != null && a.Tool == "text" && a.Text != null && a.Text.Length > 0);
    }

    bool OnInk(Anno a, Point p, float tol)
    {
        if (a.Tool == "text")
        {
            var tb = TextBounds(a);
            if (tb.Width <= 0) return false;
            tb.Inflate((int)tol, (int)tol);
            return tb.Contains(p);
        }
        var first = a.Points[0];
        var last = a.Points[a.Points.Count - 1];
        if (a.Tool == "rect")
        {
            var r = Normalize(first, last);
            // A filled box is solid ink, so its whole area is clickable; a stroked one
            // only counts on its border.
            if (a.Filled)
            {
                var solid = r; solid.Inflate((int)tol, (int)tol);
                return solid.Contains(p);
            }
            var outer = r; outer.Inflate((int)tol, (int)tol);
            var inner = r; inner.Inflate(-(int)tol, -(int)tol);
            return outer.Contains(p) && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p));
        }
        if (a.Tool == "ellipse")
        {
            var r = Normalize(first, last);
            float rx = r.Width / 2f, ry = r.Height / 2f;
            if (rx < 1f || ry < 1f) return false;
            float nx = (p.X - (r.Left + rx)) / rx, ny = (p.Y - (r.Top + ry)) / ry;
            float d = (float)Math.Sqrt(nx * nx + ny * ny);
            if (a.Filled) return d <= 1f + tol / Math.Min(rx, ry);
            return Math.Abs(d - 1f) <= tol / Math.Min(rx, ry);
        }
        return DistToSegment(p, first, last) <= tol;
    }

    // Top-most selectable annotation whose ink is under the given point.
    Anno LineUnder(Point p)
    {
        for (int i = annos.Count - 1; i >= 0; i--)
        {
            var a = annos[i];
            if (!Selectable(a)) continue;
            if (OnInk(a, p, 8f)) return a;
        }
        return null;
    }

    // Ink settings, one pair per tool: the option panel edits whichever tool is active.
    static readonly string[] InkTools = { "pen", "line", "arrow", "rect", "ellipse", "text" };
    readonly System.Collections.Generic.Dictionary<string, int> inkWidths =
        new System.Collections.Generic.Dictionary<string, int>();
    readonly System.Collections.Generic.Dictionary<string, Color> inkColors =
        new System.Collections.Generic.Dictionary<string, Color>();
    readonly System.Collections.Generic.Dictionary<string, bool> inkFills =
        new System.Collections.Generic.Dictionary<string, bool>();

    bool HasInkPanel(string name) { return Array.IndexOf(InkTools, name) >= 0; }

    bool InkFilled
    {
        get { bool f; return inkFills.TryGetValue(tool, out f) && f; }
    }

    // Only closed shapes can be filled; a line or a pen stroke has no inside.
    static bool HasFill(string name)
    {
        return name == "rect" || name == "ellipse";
    }

    bool ToolCanFill
    {
        get { return HasFill(tool); }
    }

    // The same three dots mean stroke width for shapes and font size for text.
    int FontSizeFor(int width)
    {
        return width <= 2 ? 15 : width <= 5 ? 21 : 30;
    }

    int InkWidth
    {
        get { int w; return inkWidths.TryGetValue(tool, out w) ? w : 5; }
    }

    Color InkColor
    {
        get { Color c; return inkColors.TryGetValue(tool, out c) ? c : Color.FromArgb(255, 59, 48); }
    }

    void SaveInk()
    {
        if (closing || IsDisposed) return;
        inkWidths[tool] = bar.Strip.PenWidth;
        inkColors[tool] = bar.Strip.PenColor;
        inkFills[tool] = bar.Strip.PenFill;
    }

    // Clicking the active tool again clears it: with no tool selected the frame can be
    // moved or resized again, which is what WeChat does.
    void ToggleTool(string name)
    {
        if (closing || IsDisposed) return;
        SetTool(tool == name ? "select" : name);
    }

    void SetTool(string name)
    {
        if (closing || IsDisposed) return;
        tool = name;
        if (bar != null)
        {
            bar.Selected = name;
            bool panel = HasInkPanel(name);
            if (panel)
            {
                bar.Strip.PenWidth = InkWidth;
                bar.Strip.PenColor = InkColor;
                bar.Strip.PenFill = InkFilled;
                bar.Strip.ShowFill = HasFill(name);
            }
            bar.SetOptions(panel);
            if (bar.Visible) PositionChrome();
        }
    }

    // Commit a fresh frame: edit mode starts with no tool selected, so dragging
    // inside the rectangle moves it instead of drawing.
    void CommitFrame()
    {
        tool = "select";
        bar.Selected = null;
        ShowBar();
        SetLoupeVisible(false);
    }

    // Save the framed area (with annotations) as a PNG the user picks.
    void SaveToFile()
    {
        if (closing || IsDisposed) return;
        if (!hasSel || sel.Width < 2 || sel.Height < 2) return;
        using (var dlg = new SaveFileDialog())
        {
            dlg.Filter = "PNG 图片|*.png";
            dlg.FileName = "截图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using (var bmp = RenderResult()) bmp.Save(dlg.FileName, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Program.T("保存失败：", "Save failed: ") + ex.Message, Program.T("截图", "Screenshot"));
            }
        }
    }
    void PositionChrome()
    {
        if (closing || IsDisposed) return;
        if (bar != null)
        {
            int x, y;
            if (hasSel && sel.Width > 2)
            {
                x = sel.Right - bar.Width;
                if (x < 4) x = sel.Left;
                if (x + bar.Width > ClientSize.Width) x = Math.Max(4, ClientSize.Width - bar.Width - 4);
                y = sel.Bottom + 8;
                if (y + bar.Height > ClientSize.Height - 4) y = sel.Top - bar.Height - 8;
                if (y < 4) y = Math.Max(4, ClientSize.Height - bar.Height - 8);
            }
            else
            {
                x = Math.Max(0, (ClientSize.Width - bar.Width) / 2);
                y = Math.Max(0, ClientSize.Height - bar.Height - 22);
            }
            bar.Bounds = new Rectangle(origin.X + x, origin.Y + y, bar.Width, bar.Height);
        }
        if (hint != null)
        {
            hint.Left = Math.Max(0, (ClientSize.Width - hint.Width) / 2);
            hint.Top = 14;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing = true;
        Program.TLog("close: editor");
        if (editor != null)
        {
            try { editor.Done = null; editor.Cancelled = null; editor.Close(); } catch (Exception) { }
            editor = null;
        }
        UninstallMouseHook();
        Program.TLog("close: hook done");
        if (Perf)
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snip-perf.txt"),
                    "paints=" + perfPaints + " avgMs=" + (perfPaints == 0 ? 0 : (perfTicks / 10000.0 / perfPaints)).ToString("F2")
                    + " blitMs=" + (perfPaints == 0 ? 0 : (perfBlitTicks / 10000.0 / perfPaints)).ToString("F2")
                    + " frameMs=" + (perfPaints == 0 ? 0 : (perfFrameTicks / 10000.0 / perfPaints)).ToString("F2")
                    + " pxPerPaint=" + (perfPaints == 0 ? 0 : perfPixels / perfPaints)
                    + " rectsPerPaint=" + (perfPaints == 0 ? 0 : (double)perfRects / perfPaints).ToString("F2")
                    + " maxRect=" + perfMaxW + "x" + perfMaxH
                    + " dirtyPx=" + (perfDirtyN == 0 ? 0 : perfDirtyPx / perfDirtyN)
                    + " dirtyN=" + perfDirtyN);
            }
            catch (Exception) { }
        }
        if (hideDsh && dshWnd != IntPtr.Zero) Program.ShowWindow(dshWnd, 9);
        Program.TLog("close: bar");
        if (bar != null) { bar.Hide(); bar.Close(); }
        Program.TLog("close: bar done");
        Program.TLog("close: gdi");
        DisposeGdi();
        DisposeBlit();
        Program.TLog("close: gdi done");
        Program.TLog("close: loupe");
        if (loupe != null) { loupe.Close(); loupe = null; }
        Program.TLog("close: loupe done");
        Program.TLog("close: before base");
        base.OnFormClosed(e);
        Program.TLog("close: after base");
        try { Application.ExitThread(); } catch (Exception) { }
    }

    protected override void OnResize(EventArgs e)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        base.OnResize(e);
        PositionChrome();
    }

    [StructLayout(LayoutKind.Sequential)] struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

    static Cursor penCursor;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    // The tool is spawned detached from the app, so Windows refuses it the foreground
    // and the keyboard (Esc/Enter/Ctrl+Z) stays dead until the user clicks once.
    // Attaching to the current foreground thread lifts that restriction.
    void ForceForeground()
    {
        try
        {
            var fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != myThread) attached = AttachThreadInput(fgThread, myThread, true);
            try
            {
                SetForegroundWindow(Handle);
                Activate();
                Focus();
                // Raising the overlay at show time also raises it over the magnifier,
                // which is a separate top-most window; put that back on top.
                if (loupe != null && loupe.Visible) loupe.BringToFront();
            }
            finally
            {
                if (attached) AttachThreadInput(fgThread, myThread, false);
            }
        }
        catch (Exception) { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        BringToFront();
        ForceForeground();
        // One retry: the shell occasionally hands the foreground back a moment later.
        var retry = new System.Windows.Forms.Timer { Interval = 90 };
        retry.Tick += (s, ev) =>
        {
            retry.Stop(); retry.Dispose();
            if (!ContainsFocus) ForceForeground();
        };
        retry.Start();
        lastMouse = PointToClient(Cursor.Position);
        if (!ContainsFocus) ForceForeground();
        lastLoupe = LoupeBounds(lastMouse);
        loupe = new LoupeForm(screen, origin);
        loupe.Show(this);
        lastScreen = Cursor.Position;
        loupe.SetPoint(lastScreen);
        Invalidate();
    }

    Rectangle Normalize(Point a, Point b)
    {
        return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    Rectangle[] Handles()
    {
        int s = 12;
        int cx = sel.Left + sel.Width / 2, cy = sel.Top + sel.Height / 2;
        return new[]
        {
            new Rectangle(sel.Left - s/2, sel.Top - s/2, s, s),
            new Rectangle(sel.Right - s/2, sel.Top - s/2, s, s),
            new Rectangle(sel.Left - s/2, sel.Bottom - s/2, s, s),
            new Rectangle(sel.Right - s/2, sel.Bottom - s/2, s, s),
            new Rectangle(cx - s/2, sel.Top - s/2, s, s),
            new Rectangle(cx - s/2, sel.Bottom - s/2, s, s),
            new Rectangle(sel.Left - s/2, cy - s/2, s, s),
            new Rectangle(sel.Right - s/2, cy - s/2, s, s),
        };
    }

    int HitHandle(Point p)
    {
        var hs = Handles();
        for (int i = 0; i < hs.Length; i++) if (hs[i].Contains(p)) return i;
        return -1;
    }

    // Cached: this runs for every window under the pointer on every mouse move.
    static readonly int MyPid = Process.GetCurrentProcess().Id;
    readonly StringBuilder hoverClass = new StringBuilder(256);

    void UpdateHover(Point p)
    {
        IntPtr h = GetWindow(Handle, 2);
        while (h != IntPtr.Zero)
        {
            if (IsWindowVisible(h))
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)MyPid) { h = GetWindow(h, 2); continue; }
                if ((GetWindowLong(h, -20) & 0x20) != 0) { h = GetWindow(h, 2); continue; }
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) { h = GetWindow(h, 2); continue; }
                RECT r;
                if (GetWindowRect(h, out r) && r.Right > r.Left && r.Bottom > r.Top)
                {
                    if (p.X >= r.Left - origin.X && p.X < r.Right - origin.X && p.Y >= r.Top - origin.Y && p.Y < r.Bottom - origin.Y)
                    {
                        hoverClass.Length = 0;
                        GetClassName(h, hoverClass, 256);
                        string cls = hoverClass.ToString();
                        if (cls != "Progman" && cls != "WorkerW")
                        {
                            IntPtr root = GetAncestor(h, 2);
                            if (root != IntPtr.Zero && root != Handle)
                            {
                                RECT rr = new RECT();
                                int dwm = DwmGetWindowAttribute(root, 9, out rr, Marshal.SizeOf(typeof(RECT)));
                                if (dwm != 0 && !GetWindowRect(root, out rr))
                                {
                                    h = GetWindow(h, 2);
                                    continue;
                                }
                                var rect = Rectangle.FromLTRB(rr.Left - origin.X, rr.Top - origin.Y, rr.Right - origin.X, rr.Bottom - origin.Y);
                                rect.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                                if (rect.Width > 0 && rect.Height > 0)
                                {
                                    hoverRect = rect;
                                    hasHover = true;
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            h = GetWindow(h, 2);
        }
        hoverRect = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        hasHover = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        // Right click cancels, like every other snipping tool; swallowing it here also
        // keeps the desktop context menu from appearing over the overlay.
        if (e.Button == MouseButtons.Right) { Close(); return; }
        base.OnMouseDown(e);
        // The hook already ended text editing for this very click: do not treat it as a
        // fresh click on the frame (that is what opened yet another text box).
        if (swallowClick.HasValue)
        {
            var sc = swallowClick.Value;
            bool recent = (DateTime.Now - swallowAt).TotalMilliseconds < 800;
            swallowClick = null;
            // Only the click that would open another text box is eaten; a press on a
            // handle (all eight) or on the frame body must still resize / move it.
            if (recent && Math.Abs(e.X - sc.X) <= 4 && Math.Abs(e.Y - sc.Y) <= 4
                && HitHandle(e.Location) < 0 && tool == "text" && hasSel && sel.Contains(e.Location)) return;
        }
        // Clicking the overlay activates and raises it, which would hide the magnifier
        // behind the dim layer for the whole drag; put it back on top (it is click-through).
        KeepBarOnTop();
        if (loupe != null) loupe.BringToFront();
        if (!ContainsFocus) ForceForeground();

        // Lines behave like objects: click one to select it (endpoints appear), then
        // drag its body or an endpoint. Clicking empty space clears the selection.
        if (e.Button == MouseButtons.Left && hasSel)
        {
            // Frame handles first: they sit on the edge, where an annotation can overlap
            // them, and losing that click made the frame impossible to shrink past it.
            int fh = HitHandle(e.Location);
            if (fh >= 0)
            {
                HideBar();
                SetLoupeVisible(false);
                dragMode = 3;
                handle = fh;
                dragStart = e.Location;
                selStart = sel;
                return;
            }
            if (Adjustable(activeAnno))
            {
                var hs = HandlesOf(activeAnno);
                for (int i = 0; i < hs.Length; i++)
                    if (Dist(e.Location, hs[i]) <= 12f)
                    {
                        activeEnd = i;
                        dragUndoPoints = new List<Point>(activeAnno.Points);
                        SetLoupeVisible(false);
                        dragMode = 5;
                        shapeStart = Normalize(activeAnno.Points[0], activeAnno.Points[activeAnno.Points.Count - 1]);
                        Invalidate();
                        return;
                    }
            }
            // Pressing straight on a handle grabs it, even when the shape is not the
            // selected one yet: that is what the user sees and aims at.
            for (int i = annos.Count - 1; i >= 0; i--)
            {
                var a = annos[i];
                if (!Adjustable(a)) continue;
                int hIdx = HandleAt(a, e.Location);
                if (hIdx < 0) continue;
                activeAnno = a;
                activeEnd = hIdx;
                dragUndoPoints = new List<Point>(a.Points);
                SetLoupeVisible(false);
                dragMode = 5;
                shapeStart = Normalize(a.Points[0], a.Points[a.Points.Count - 1]);
                Invalidate();
                return;
            }
            for (int i = annos.Count - 1; i >= 0; i--)
            {
                var a = annos[i];
                if (!Selectable(a)) continue;
                // The pointer has to be on this annotation before anything happens to it --
                // without this the double-click opened whichever text was created last.
                if (!OnInk(a, e.Location, 8f)) continue;
                // Editing needs a real double-click: one click selects the text (and lets
                // it be dragged), like WeChat. A slow second click must not open the box.
                if (a.Tool == "text" && e.Clicks >= 2)
                {
                    pendingEditorAt = a.Points[0];
                    pendingEditorTarget = a;
                    return;
                }
                {
                    activeAnno = a;
                    activeEnd = -1;
                    dragUndoPoints = new List<Point>(a.Points);
                    moveBox = AnnoBounds(a);
                    if (a.Tool == "text" && !moveBox.IsEmpty) moveBox = TextClampBox(moveBox);
                    // Dragging a text is a text action too: keep the toolbar in step.
                    if (a.Tool == "text" && tool != "text") SetTool("text");
                    SetLoupeVisible(false);
                    dragMode = 6;
                    dragStart = e.Location;
                    bodyStart = a.Points.ToArray();
                    Invalidate();
                    return;
                }
            }
            if (activeAnno != null) { activeAnno = null; activeEnd = -1; Invalidate(); }
        }
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        // Clicking outside the open text box commits it. Only the click that would open
        // another text box is swallowed; a click on a handle (or on empty space) keeps
        // going, otherwise resizing needs two clicks and the magnifier pops up first.
        if (editor != null)
        {
            Program.TLog("down at=" + e.Location + " editor=open tool=" + tool + " hasSel=" + hasSel +
                " inSel=" + (hasSel && sel.Contains(e.Location)));
            var ed = editor;
            ed.CommitText();
            Program.TLog("after CommitText editor=" + (editor != null ? "still open" : "closed") + " tool=" + tool);
            // If the commit never came back (the form stayed alive), the leftover window
            // would keep swallowing clicks forever -- close it by hand and leave text mode.
            if (editor != null)
            {
                Program.TLog("force close: Done never fired");
                editor = null;
                editingAnno = null;
                try { ed.Dispose(); } catch (Exception) { }
                SetTool("select");
                Invalidate();
            }
            // A handle press must still resize after committing: that click is how the
            // user leaves text editing and starts dragging the frame.
            if (HitHandle(e.Location) < 0 && tool == "text" && hasSel && sel.Contains(e.Location)) return;
        }

        if (!hasSel && hasHover && hoverRect.Width > 0)
        {
            // WeChat drops the magnifier once the frame is committed: it comes back
            // only while a new frame is being drawn, not while editing.
            SetLoupeVisible(true);
            dragMode = 4;
            dragStart = e.Location;
            return;
        }

        if (hasSel)
        {
            int h = HitHandle(e.Location);
            if (h >= 0)
            {
                HideBar();
                SetLoupeVisible(true);    // resizing needs the magnifier, like WeChat
                dragMode = 3;
                handle = h;
                dragStart = e.Location;
                selStart = sel;
                return;
            }
            if (sel.Contains(e.Location))
            {
                if (tool == "select") { HideBar(); dragMode = 2; dragStart = e.Location; selStart = sel; return; }
                if (tool == "number")
                {
                    int n = 1;
                    foreach (var a in annos) if (a.Tool == "number") n++;
                    {
                    var na = new Anno { Tool = "number", Points = { e.Location }, Number = n };
                    annos.Add(na);
                    undoSteps.Add(new UndoStep { Anno = na, Points = null });
                }
                    Invalidate();
                    return;
                }
                if (tool == "text")
                {
                    KeepBarOnTop();
                    pendingEditorAt = e.Location;
                    pendingEditorTarget = null;
                    return;
                }
                draft = new Anno { Tool = tool, Points = { e.Location }, Width = InkWidth, Color = InkColor, Filled = ToolCanFill && InkFilled };
                Invalidate();
                return;
            }
        }
        // A finished frame must survive a click outside it: that click used to throw the
        // whole selection away and start a new snipping pass.
        if (hasSel) return;
        SetLoupeVisible(true);
        HideBar();
        dragMode = 1;
        dragStart = e.Location;
        hasSel = false;
        paintedSel = Rectangle.Empty;
        sel = new Rectangle(e.Location, Size.Empty);
        HideBar();
        Invalidate();
    }

    void SetLoupeVisible(bool visible)
    {
        if (loupe == null) return;
        if (visible) { if (!loupe.Visible) loupe.Show(); cursorTimer.Start(); }
        else
        {
            if (loupe.Visible) loupe.Hide();
            if (dragMode == 0) cursorTimer.Stop();
        }
    }

    long lastLoupeAt;

    void TrackLoupe()
    {
        if (loupe == null || !loupe.Visible) return;
        var sp = Cursor.Position;
        if (sp == lastScreen) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastLoupeAt != 0 && now - lastLoupeAt < System.Diagnostics.Stopwatch.Frequency / 60) return;
        lastLoupeAt = now;
        lastScreen = sp;
        loupe.SetPoint(sp);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        base.OnMouseMove(e);
        // The 16ms timer is a WM_TIMER, which Windows only delivers when the queue
        // is empty; while dragging the queue is never empty, so the magnifier used
        // to freeze. Follow the mouse from the event itself instead.
        TrackLoupe();
        lastMouse = e.Location;
        // Resizing shows the magnifier so the edge can be aimed; moving the frame or an
        // annotation does not (it would sit on top of the thing being dragged).
        if (dragMode == 3) SetLoupeVisible(true);
        else if (hasSel && (dragMode == 2 || dragMode == 5 || dragMode == 6)) SetLoupeVisible(false);
        if (dragMode == 4)
        {
            if (Math.Abs(e.X - dragStart.X) > 4 || Math.Abs(e.Y - dragStart.Y) > 4)
            {
                dragMode = 1;
                hasSel = false;
                HideBar();
                // The pending-click state painted the hovered window bright; that
                // chrome has to be erased before incremental drag repaints begin.
                Invalidate();
            }
            else
            {
                return;
            }
        }
        if (dragMode == 1)
        {
            var old = sel;
            sel = Normalize(dragStart, e.Location);
            InvalidateChrome(paintedSel, sel);
            return;
        }
        if (dragMode == 2)
        {
            var old = sel;
            int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
            int x = Math.Max(0, Math.Min(ClientSize.Width - selStart.Width, selStart.X + dx));
            int y = Math.Max(0, Math.Min(ClientSize.Height - selStart.Height, selStart.Y + dy));
            // Same containment rule as resizing, applied to moving: the frame owns what
            // it contains, so it can only slide as far as the annotations stay inside it.
            // The frame stops as soon as one of its edges reaches an annotation.
            var inside = ClampBox();
            if (inside.Width > 0 && selStart.Contains(inside))
            {
                inside.Inflate(6, 6);
                x = Math.Max(inside.Right - selStart.Width, Math.Min(inside.Left, x));
                y = Math.Max(inside.Bottom - selStart.Height, Math.Min(inside.Top, y));
                x = Math.Max(0, x);
                y = Math.Max(0, y);
            }
            sel = new Rectangle(x, y, selStart.Width, selStart.Height);
            InvalidateChrome(paintedSel, sel);
            return;
        }
        if (dragMode == 3)
        {
            int l = selStart.Left, t = selStart.Top, r = selStart.Right, b = selStart.Bottom;
            // Handle map: 0 TL, 1 TR, 2 BL, 3 BR, 4 top, 5 bottom, 6 left, 7 right.
            // Each handle moves only its own edges, and an edge is clamped so it can
            // never cross the opposite one (that produced inverted rectangles).
            if (handle == 0 || handle == 2 || handle == 6) l = Math.Min(e.X, r - 6);
            if (handle == 1 || handle == 3 || handle == 7) r = Math.Max(e.X, l + 6);
            if (handle == 0 || handle == 1 || handle == 4) t = Math.Min(e.Y, b - 6);
            if (handle == 2 || handle == 3 || handle == 5) b = Math.Max(e.Y, t + 6);
            // An annotation belongs to the frame: shrinking must not cut it out. Each edge
            // stops a few pixels short of the annotation instead (WeChat leaves the same
            // gap), which is the containment the frame is supposed to keep.
            var inside = ClampBox();
            if (inside.Width > 0 && selStart.Contains(inside))
            {
                inside.Inflate(6, 6);
                l = Math.Min(l, inside.Left);
                t = Math.Min(t, inside.Top);
                r = Math.Max(r, inside.Right);
                b = Math.Max(b, inside.Bottom);
            }
            sel = Rectangle.FromLTRB(
                Math.Max(0, Math.Min(l, r - 6)), Math.Max(0, Math.Min(t, b - 6)),
                Math.Min(ClientSize.Width, Math.Max(r, l + 6)), Math.Min(ClientSize.Height, Math.Max(b, t + 6)));
            // The toolbar is hidden while resizing; repositioning it (and rebuilding
            // its window region) every mouse move was pure overhead.
            InvalidateChrome(paintedSel, sel);
            return;
        }
        if (dragMode == 6 && activeAnno != null && bodyStart != null && bodyStart.Length == activeAnno.Points.Count)
        {
            int ddx = e.X - dragStart.X, ddy = e.Y - dragStart.Y;
            // The content belongs to the frame too: it cannot be dragged out of it.
            if (!moveBox.IsEmpty && sel.Width > 12 && sel.Height > 12)
            {
                ddx = Math.Max(sel.Left + 6 - moveBox.Left, Math.Min(sel.Right - 6 - moveBox.Right, ddx));
                ddy = Math.Max(sel.Top + 6 - moveBox.Top, Math.Min(sel.Bottom - 6 - moveBox.Bottom, ddy));
            }
            for (int i = 0; i < bodyStart.Length; i++)
                activeAnno.Points[i] = new Point(bodyStart[i].X + ddx, bodyStart[i].Y + ddy);
            Invalidate();
            return;
        }
        if (dragMode == 5 && activeAnno != null && activeEnd >= 0)
        {
            if (activeAnno.Tool == "rect" || activeAnno.Tool == "ellipse") ReshapeRect(activeAnno, activeEnd, e.Location);
            else
            {
                // activeEnd is a handle index (0 = start, 1 = end), not a point index:
                // a freehand stroke has many points and only the ends are draggable.
                int pi = activeEnd == 0 ? 0 : activeAnno.Points.Count - 1;
                activeAnno.Points[pi] = e.Location;
            }
            Invalidate();
            return;
        }
        if (draft != null)
        {
            draft.Points.Add(e.Location);
            Invalidate();
            return;
        }
        if (!hasSel)
        {
            var oldHover = hoverRect;
            if (Math.Abs(e.X - lastProbe.X) + Math.Abs(e.Y - lastProbe.Y) >= 3 &&
                (DateTime.Now - lastProbeAt).TotalMilliseconds >= 40)
            {
                lastProbe = e.Location;
                lastProbeAt = DateTime.Now;
                UpdateHover(e.Location);
            }
            if (oldHover != hoverRect) { InvalidateHover(oldHover); InvalidateHover(hoverRect); }
            return;
        }
        int hh = HitHandle(e.Location);
        if (hh >= 0) Cursor = (hh == 0 || hh == 3) ? Cursors.SizeNWSE : (hh == 1 || hh == 2) ? Cursors.SizeNESW : (hh == 4 || hh == 5) ? Cursors.SizeNS : Cursors.SizeWE;
        else if (sel.Contains(e.Location) && tool == "select") Cursor = Cursors.SizeAll;
        else if (activeAnno != null && HandleAt(activeAnno, e.Location) >= 0)
        {
            var pick = HandleCursor(activeAnno, HandleAt(activeAnno, e.Location));
            Cursor = pick;
            Cursor.Current = pick;
        }
        else if (LineUnder(e.Location) != null)
        {
            Cursor = Cursors.SizeAll;
            Cursor.Current = Cursors.SizeAll;
        }
        else if (TextUnder(e.Location) != null) { Cursor = Cursors.SizeAll; Cursor.Current = Cursors.SizeAll; }
        else if (tool != "select" && tool != "" && sel.Contains(e.Location)) { Cursor = Cursors.Cross; Cursor.Current = Cursors.Cross; }
        else { Cursor = Cursors.Default; Cursor.Current = Cursors.Default; }
    }

    // The size label is painted above the frame's top-left corner, so repainting
    // only the frame itself clips the new label away and leaves the previous one
    // behind as white ghost text on the desktop.
    // Moving the highlight from one window to another used to repaint both rectangles in
    // full (a maximised window means the whole 2560x1600 screen, twice). Only the parts
    // that actually change need repainting: the union minus the shared area, plus the
    // frame lines that run through the shared area.
    void InvalidateHoverDelta(Rectangle a, Rectangle b)
    {
        const int pad = 8;
        bool oa = a.Width > 0 && a.Height > 0, ob = b.Width > 0 && b.Height > 0;
        if (!oa) { if (ob) InvalidateHover(b); return; }
        if (!ob) { InvalidateHover(a); return; }
        var inter = Rectangle.Intersect(a, b);
        if (inter.Width <= 0 || inter.Height <= 0) { InvalidateHover(a); InvalidateHover(b); return; }
        var u = Rectangle.Union(a, b);
        if (u.Top < inter.Top)
            Invalidate(new Rectangle(u.Left - pad, u.Top - pad, u.Width + 2 * pad, inter.Top - u.Top + pad));
        if (u.Bottom > inter.Bottom)
            Invalidate(new Rectangle(u.Left - pad, inter.Bottom, u.Width + 2 * pad, u.Bottom - inter.Bottom + pad));
        if (u.Left < inter.Left)
            Invalidate(new Rectangle(u.Left - pad, inter.Top, inter.Left - u.Left + pad, inter.Height));
        if (u.Right > inter.Right)
            Invalidate(new Rectangle(inter.Right, inter.Top, u.Right - inter.Right + pad, inter.Height));
        HoverFrameStrips(a, inter);
        HoverFrameStrips(b, inter);
    }

    // The 5px band along each edge of r that lies inside the shared area: the old frame has
    // to be erased there and the new one drawn.
    void HoverFrameStrips(Rectangle r, Rectangle inter)
    {
        const int w = 6;
        if (r.Top > inter.Top && r.Top < inter.Bottom)
            Invalidate(new Rectangle(inter.Left, r.Top - 3, inter.Width, w));
        if (r.Bottom > inter.Top && r.Bottom < inter.Bottom)
            Invalidate(new Rectangle(inter.Left, r.Bottom - 3, inter.Width, w));
        if (r.Left > inter.Left && r.Left < inter.Right)
            Invalidate(new Rectangle(r.Left - 3, inter.Top, w, inter.Height));
        if (r.Right > inter.Left && r.Right < inter.Right)
            Invalidate(new Rectangle(r.Right - 3, inter.Top, w, inter.Height));
    }

    void InvalidateHover(Rectangle r)
    {
        var area = r;
        area.Inflate(8, 8);
        area = Rectangle.Union(area, new Rectangle(r.Left - 8, r.Top - 38, Math.Max(160, r.Width + 16), 46));
        Invalidate(area);
    }

    // Frame (6px), handles (12px) and the size label above the top edge all stick
    // out of the rectangle, so a region-based repaint has to include them.
    // Where the size label actually lands, including the clamp that pushes it inside
    // the rectangle when there is no room above (dragging towards the screen top).
    Rectangle LabelArea(Rectangle r)
    {
        int w = Math.Max(200, r.Width + 20);
        int x = r.Left - 6;
        int y = r.Top - 40;
        if (y < 0) y = r.Top + 2;
        if (x + w > ClientSize.Width) x = ClientSize.Width - w;
        return new Rectangle(x, y, w, 44);
    }

    Rectangle ChromeArea(Rectangle r)
    {
        var area = Rectangle.Inflate(r, 14, 14);
        return Rectangle.Union(area, LabelArea(r));
    }

    // Dragging used to invalidate the whole 2560x1600 surface on every mouse move,
    // which starved the UI thread and left the magnifier behind the cursor. Only
    // the ring plus the strip that changed between the two rectangles has to be
    // repainted: everything inside both is already the bright screenshot.
    Rectangle paintedSel;
    readonly List<Rectangle> pendingDirty = new List<Rectangle>();
    long lastChromeAt;

    void InvalidateChrome(Rectangle oldSel, Rectangle newSel)
    {
        // The selection frame only needs to look continuous at ~30fps; the magnifier
        // is updated from every mouse event and must not wait behind these repaints.
        // 60fps so the frame moves with the display; the magnifier no longer depends
        // on this path (it is driven by mouse events), so the cost is affordable.
        long tick = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastChromeAt != 0 && tick - lastChromeAt < System.Diagnostics.Stopwatch.Frequency / 60) return;
        lastChromeAt = tick;
        using (var dirty = new Region(ChromeArea(oldSel)))
        {
            dirty.Union(ChromeArea(newSel));
            // Skip the interior that is bright in both frames, but keep a 10px ring so
            // the old frame line (6px wide, centred on the edge) is painted over.
            var unchanged = Rectangle.Intersect(oldSel, newSel);
            unchanged.Inflate(-10, -10);
            if (unchanged.Width > 0 && unchanged.Height > 0) dirty.Exclude(unchanged);
            // The label can sit inside the rectangle (clamped near the top edge), so
            // re-add both label areas after the interior was excluded: otherwise the
            // growing rectangle leaves a smear of old size labels behind it.
            dirty.Union(LabelArea(oldSel));
            dirty.Union(LabelArea(newSel));
            // Remember the region's own rectangles: the bounding box of an L-shaped
            // strip is as big as the whole frame, which is why growing by the
            // bottom-right handle used to cost far more than a single edge.
            // Accumulate, never clear here: a paint may still be pending for the
            // previous batch, and dropping it leaves those strips unpainted (the
            // back-and-forth drag left blue frame remnants exactly this way).
            foreach (var sc in dirty.GetRegionScans(new Matrix()))
            {
                pendingDirty.Add(Rectangle.FromLTRB((int)sc.Left, (int)sc.Top, (int)sc.Right, (int)sc.Bottom));
            }
            Invalidate(dirty);
        }
        // Advance on request, not on paint: a magnifier-driven partial repaint must
        // not move this marker, or the swept frames between requests stay behind.
        paintedSel = newSel;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        base.OnMouseUp(e);
        if (pendingEditorAt.HasValue)
        {
            var at = pendingEditorAt.Value;
            var target = pendingEditorTarget;
            pendingEditorAt = null;
            pendingEditorTarget = null;
            OpenEditor(at, target);
            return;
        }
        if (dragMode == 4)
        {
            sel = hoverRect;
            hasSel = sel.Width >= 6 && sel.Height >= 6;
            if (hasSel) CommitFrame();
            dragMode = 0;
            Invalidate();
            return;
        }
        if (dragMode == 1)
        {
            hasSel = sel.Width >= 6 && sel.Height >= 6;
            if (hasSel) CommitFrame();
            Invalidate();
        }
        else if (dragMode == 2 || dragMode == 3)
        {
            // Moving or resizing the frame is still "framing": the toolbar comes back
            // once the mouse is released, like WeChat.
            hasSel = sel.Width >= 6 && sel.Height >= 6;
            if (hasSel) { ShowBar(); SetLoupeVisible(false); }
            Invalidate();
        }
        else if (dragMode == 5 || dragMode == 6)
        {
            // A finished move/resize is one undo step: Ctrl+Z puts the shape back.
            if (activeAnno != null && dragUndoPoints != null && !SamePoints(dragUndoPoints, activeAnno.Points))
                undoSteps.Add(new UndoStep { Anno = activeAnno, Points = dragUndoPoints });
            dragUndoPoints = null;
            dragMode = 0;
            Invalidate();
            return;
        }
        else if (draft != null)
        {
            if (draft.Points.Count > 1 &&
                Dist(draft.Points[0], draft.Points[draft.Points.Count - 1]) >= 4f)
            {
                annos.Add(draft);
                undoSteps.Add(new UndoStep { Anno = draft, Points = null });
                if (bar != null) bar.Strip.Invalidate();   // the undo button becomes usable
            }
            activeAnno = null;
            activeEnd = -1;
            draft = null;
            Invalidate();
        }
        dragMode = 0;
        if (loupe == null || !loupe.Visible) cursorTimer.Stop();
        KeepBarOnTop();
    }

    // Clicking the overlay makes it the foreground top-most window, which pushes the
    // toolbar (also top-most) behind it. Put it back after every mouse interaction.
    void KeepBarOnTop()
    {
        if (closing || IsDisposed) return;
        if (bar != null && bar.Visible) bar.BringToFront();
    }

    Anno editingAnno;
    TextEditorForm editor;

    // Opens the stand-alone editor at the click point. Editing an existing text reuses
    // its font, colour and wrap width.
    void OpenEditor(Point at, Anno target)
    {
        Program.TLog("OpenEditor at=" + at + " target=" + (target != null ? "yes" : "no") + " sel=" + sel);
        if (editor != null) return;
        int px = target != null ? (target.Width > 0 ? target.Width : 16) : FontSizeFor(bar.Strip.PenWidth);
        var fore = target != null && !target.Color.IsEmpty ? target.Color : bar.Strip.PenColor;
        var back = Color.White;
        try
        {
            var px2 = new Point(at.X - origin.X + 6, at.Y - origin.Y + 6);
            if (screen != null && px2.X >= 0 && px2.Y >= 0 && px2.X < screen.Width && px2.Y < screen.Height)
                back = screen.GetPixel(px2.X, px2.Y);
        }
        catch (Exception) { }

        // Entering text editing must put the toolbar on the text tool, whatever was
        // armed before: the two states have to agree.
        if (tool != "text") SetTool("text");
        editingAnno = target;
        LastEditorX = at.X;
        LastEditorY = at.Y;
        // Grow right until the frame's right edge, then wrap and grow down: the
        // 12px covers the frame line plus the editor's own 4px pad on each side.
        int growTo = hasSel ? sel.Right - at.X - 12 : 0;
        // Once the box is tall enough to reach the frame's bottom it slides up, so
        // the line being typed never leaves the selection.
        var keepInside = hasSel
            ? new Rectangle(origin.X + sel.Left, origin.Y + sel.Top + 8, sel.Width, sel.Height - 16)
            : Rectangle.Empty;
        editor = new TextEditorForm(new Font("Microsoft YaHei UI", px), fore, back, growTo, keepInside);
        editor.Location = new Point(Left + at.X - 4, Top + at.Y - 4);
        editor.Done = text => FinishEditor(text);
        editor.Cancelled = () => { editor = null; editingAnno = null; Invalidate(); };
        if (target != null) editor.Box.Text = target.Text ?? "";
        SetLoupeVisible(false);   // the magnifier would sit on top of the editor
        editor.Show(this);
        Invalidate();
    }

    void FinishEditor(string text)
    {
        Program.TLog("FinishEditor len=" + (text == null ? 0 : text.Length) + " target=" + (editingAnno != null ? "yes" : "no"));
        var target = editingAnno;
        var box = editor;
        editor = null;
        editingAnno = null;
        // The box slides up to stay inside the frame, so the click point is not where
        // the text is any more: draw it where the box finally sat.
        int textX = LastEditorX, textY = LastEditorY;
        if (box != null && !box.ResultLocation.IsEmpty)
        {
            textX = box.ResultLocation.X - Left + 4;
            textY = box.ResultLocation.Y - Top + 4;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (target != null)
            {
                target.Text = text;
                target.Points[0] = new Point(textX, textY);
                if (box != null && box.WrapWidth > 0) target.BoxW = box.WrapWidth;
            }
            else
            {
                var a = new Anno
                {
                    Tool = "text",
                    Points = { new Point(textX, textY) },
                    Text = text,
                    Width = FontSizeFor(bar.Strip.PenWidth),
                    Color = box != null && !box.Box.ForeColor.IsEmpty ? box.Box.ForeColor : bar.Strip.PenColor,
                    BoxW = box != null && box.WrapWidth > 0 ? box.WrapWidth : 200,
                };
                annos.Add(a);
                undoSteps.Add(new UndoStep { Anno = a, Points = null });
            }
        }
        // Committing text must NOT bring the magnifier back: it belongs to drawing a new
        // frame, and popping it up over a finished note is just noise.
        SetLoupeVisible(false);
        Invalidate();
    }

    int LastEditorX, LastEditorY;
    Point? pendingEditorAt;
    Anno pendingEditorTarget;

    Font TextFont(int px)
    {
        return new Font("Microsoft YaHei UI", px);
    }

    // Re-open an existing text: it is hidden while the box is up, and the committed
    // text goes back into the same object instead of creating a second one.
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM cf);
    [DllImport("imm32.dll")] static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CANDIDATEFORM cf);

    [StructLayout(LayoutKind.Sequential)]
    struct CANDIDATEFORM
    {
        public int dwIndex;
        public int dwStyle;
        public int x;
        public int y;
        public int areaLeft;
        public int areaTop;
        public int areaRight;
        public int areaBottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COMPOSITIONFORM
    {
        public int dwStyle;
        public int x;
        public int y;
        public int areaLeft;
        public int areaTop;
        public int areaRight;
        public int areaBottom;
    }

    // The input-method candidate list followed the screen corner because the edit box
    // never told the IME where its caret is. Pin it to the caret, every time.
    // Make the overlay the active window and give the edit box the keyboard. Without
    // this the IME composes in a floating window pinned to the screen corner.
    void ActivateEditor()
    {
        try
        {
            ForceForeground();
            SetActiveWindow(Handle);
            textBox.Visible = true;
            MatchTextBackground();
            textBox.Focus();
            SetFocus(textBox.Handle);
            PlaceImeWindow();
            Invalidate(); // draw the dashed outline around the box
        }
        catch (Exception) { }
    }

    void PlaceImeWindow()
    {
        try
        {
            if (textBox == null || !textBox.Visible) return;
            var hIMC = ImmGetContext(textBox.Handle);
            if (hIMC == IntPtr.Zero) return;
            var cf = new COMPOSITIONFORM
            {
                dwStyle = 2, // CFS_POINT: composition string at the caret
                x = 4,
                y = textBox.Font.Height,
            };
            ImmSetCompositionWindow(hIMC, ref cf);

            // CFS_CANDIDATEPOS = 0x40, coordinates are screen pixels.
            var caret = textBox.PointToScreen(new Point(4, textBox.Font.Height + 4));
            var cand = new CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = 0x40,
                x = caret.X,
                y = caret.Y,
            };
            ImmSetCandidateWindow(hIMC, ref cand);
            ImmReleaseContext(textBox.Handle, hIMC);
        }
        catch (Exception) { }
    }

    // Blend the edit box into the screenshot instead of slapping a white slab on it.
    void MatchTextBackground()
    {
        try
        {
            if (screen == null || textBox == null) return;
            // Average over the box, not one pixel: sampling a single point landed on a
            // glyph often enough to turn the whole box black.
            int x0 = textBox.Left - origin.X, y0 = textBox.Top - origin.Y;
            int bw = Math.Max(1, textBox.Width), bh = Math.Max(1, textBox.Height);
            long sr = 0, sg = 0, sb = 0; int n = 0;
            for (int gy = 0; gy < 4; gy++)
                for (int gx = 0; gx < 8; gx++)
                {
                    int sx = Math.Max(0, Math.Min(screen.Width - 1, x0 + gx * bw / 8 + bw / 16));
                    int sy = Math.Max(0, Math.Min(screen.Height - 1, y0 + gy * bh / 4 + bh / 8));
                    var px = screen.GetPixel(sx, sy);
                    sr += px.R; sg += px.G; sb += px.B; n++;
                }
            var c = Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
            int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            // Tint it slightly away from the background so the box stays visible.
            // No tint: the box must look like the screenshot itself, only the dashed
            // outline tells the user where it is (that is the WeChat look).
            textBox.BackColor = Color.FromArgb(c.R, c.G, c.B);
            textBox.ForeColor = lum > 140 ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
        }
        catch (Exception) { }
    }

    void CommitText()
    {
        if (textBox.Visible && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            var p = textBox.Location;
            if (editingAnno != null)
            {
                editingAnno.Text = textBox.Text;
                editingAnno.Points[0] = new Point(p.X, p.Y + 4);
                textBox.Visible = false;
                editingAnno = null;
                activeAnno = null;
                activeEnd = -1;
                Invalidate();
                return;
            }
            {
                var ta = new Anno
                {
                    Tool = "text",
                    Points = { new Point(p.X, p.Y + 4) },
                    Text = textBox.Text,
                    Width = FontSizeFor(bar.Strip.PenWidth),
                    Color = bar.Strip.PenColor,
                    BoxW = textBox.Width,
                };
                annos.Add(ta);
                undoSteps.Add(new UndoStep { Anno = ta, Points = null });
            }
        }
        textBox.Visible = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        base.OnKeyDown(e);
        // While the text box is open it owns Enter and Escape: Enter commits the text
        // and Escape cancels it. Without this, KeyPreview let the form capture the
        // whole screenshot on Enter and throw the selection away on Escape.
        if (textBox != null && textBox.Visible)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitText();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                editingAnno = null;
                textBox.Visible = false;
                Invalidate();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            return;
        }
        if (HandleShortcut(e.KeyCode, e.Control))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    // Shared by the overlay and the toolbar window: the toolbar is a separate
    // top-level window, so it can own the keyboard and swallow Esc / Ctrl+Z.
    public bool HandleShortcut(Keys key, bool control)
    {
        if (closing || IsDisposed) return false;
        if (textBox != null && textBox.Visible)
        {
            if (key == Keys.Enter) { CommitText(); return true; }
            if (key == Keys.Escape)
            {
                textBox.Visible = false;
                editingAnno = null;
                Invalidate();
                return true;
            }
            return false;
        }
        if (key == Keys.Escape) { Close(); return true; }
        if (control && key == Keys.Z) { UndoLast(); return true; }
        if (false)
        {
            if (undoSteps.Count > 0)
            {
                var step = undoSteps[undoSteps.Count - 1];
                undoSteps.RemoveAt(undoSteps.Count - 1);
                if (step.Points == null)
                {
                    annos.Remove(step.Anno);
                    if (activeAnno == step.Anno) { activeAnno = null; activeEnd = -1; }
                }
                else
                {
                    step.Anno.Points.Clear();
                    step.Anno.Points.AddRange(step.Points);
                }
            }
            else if (annos.Count > 0)
            {
                // Fallback for shapes drawn before this history existed.
                annos.RemoveAt(annos.Count - 1);
                activeAnno = null;
                activeEnd = -1;
            }
            Invalidate();
            return true;
        }
        // The card says "press C", so the bare key must work too (Ctrl+C still does).
        if (!control && key == Keys.C && !hasSel && screen != null && editor == null)
        {
            Color cc = screen.GetPixel(Math.Min(screen.Width - 1, Math.Max(0, lastMouse.X)),
                Math.Min(screen.Height - 1, Math.Max(0, lastMouse.Y)));
            try { Clipboard.SetText("#" + cc.R.ToString("X2") + cc.G.ToString("X2") + cc.B.ToString("X2")); }
            catch (Exception) { }
            return true;
        }
        if (control && key == Keys.C && !hasSel && screen != null)
        {
            Color c = screen.GetPixel(Math.Min(screen.Width - 1, Math.Max(0, lastMouse.X)), Math.Min(screen.Height - 1, Math.Max(0, lastMouse.Y)));
            try { Clipboard.SetText("#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2")); } catch (Exception) { }
            return true;
        }
        if (key == Keys.Enter && hasSel) { CopyAndClose(); return true; }
        return false;
    }

    Bitmap RenderResult()
    {
        var bmp = new Bitmap(sel.Width, sel.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(screen, new Rectangle(0, 0, sel.Width, sel.Height), sel, GraphicsUnit.Pixel);
            g.TranslateTransform(-sel.X, -sel.Y);
            foreach (var a in annos) DrawAnno(g, a);
        }
        return bmp;
    }

    void CopyAndClose()
    {
        if (closing || IsDisposed) return;
        if (!hasSel || sel.Width < 2 || sel.Height < 2) return;
        try
        {
            using (var bmp = RenderResult())
            {
                Clipboard.SetImage(bmp);
                // Also leave a copy on disk: the web UI picks it up and pastes it into
                // the composer, so the user never has to press Ctrl+V.
                try
                {
                    bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-snip-last.png"),
                        System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception) { }
            }
            Close();
            PasteIntoDsh();
        }
        catch (Exception)
        {
            MessageBox.Show(Program.T("复制到剪贴板失败，请重试。", "Could not copy the image. Try again."), Program.T("截图", "Screenshot"));
        }
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    // After "done" the image is already on the clipboard; bring DSH forward and press
    // Ctrl+V so it lands in the composer exactly like a manual paste.
    void PasteIntoDsh()
    {
        try
        {
            if (dshWnd == IntPtr.Zero) FindDsh();
            if (dshWnd == IntPtr.Zero) return;
            System.Threading.Thread.Sleep(220);
            Program.ShowWindow(dshWnd, 9);
            SetForegroundWindow(dshWnd);
            System.Threading.Thread.Sleep(140);
            keybd_event(0x11, 0, 0, IntPtr.Zero);      // Ctrl down
            keybd_event(0x56, 0, 0, IntPtr.Zero);      // V down
            keybd_event(0x56, 0, 2, IntPtr.Zero);      // V up
            keybd_event(0x11, 0, 2, IntPtr.Zero);      // Ctrl up
        }
        catch (Exception) { }
    }

    void DrawAnno(Graphics g, Anno a)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(255, 59, 48), 3))
        using (var brush = new SolidBrush(Color.FromArgb(255, 59, 48)))
        {
            var pts = a.Points;
            if (a.Tool == "pen")
            {
                using (var ink = new Pen(a.Color.IsEmpty ? Color.FromArgb(255, 59, 48) : a.Color, a.Width > 0 ? a.Width : 4))
                {
                    ink.StartCap = ink.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    ink.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    for (int i = 1; i < pts.Count; i++) g.DrawLine(ink, pts[i - 1], pts[i]);
                }
            }
            else if (a.Tool == "arrow" && pts.Count > 1)
            {
                var p0 = pts[0];
                var p1 = pts[pts.Count - 1];
                var ink = a.Color.IsEmpty ? Color.FromArgb(255, 59, 48) : a.Color;
                float w = a.Width > 0 ? a.Width : 5;
                using (var brush2 = new SolidBrush(ink))
                {
                    // Two perfectly straight tapers, no sampled wobble: the shaft grows
                    // from 0.1x to 2x the stroke, the head is a flat-backed triangle that
                    // runs from 4.65x at its base to 0 at the tip.
                    const int shaftSteps = 14, headSteps = 9;
                    var prof = new float[shaftSteps + headSteps + 2];
                    for (int i = 0; i <= shaftSteps; i++) prof[i] = 0.1f + (2.0f - 0.1f) * i / shaftSteps;
                    for (int i = 0; i <= headSteps; i++) prof[shaftSteps + 1 + i] = 4.65f * (1f - (float)i / headSteps);
                    float total = (float)Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y));
                    if (total < 1f) total = 1f;
                    double ang = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X);
                    float dx = (float)Math.Cos(ang), dy = (float)Math.Sin(ang);
                    float headLen = Math.Min(w * 7.6f, total * 0.7f);
                    float shaftLen = Math.Max(1f, total - headLen);
                    var poly = new PointF[prof.Length * 2];
                    for (int i = 0; i < prof.Length; i++)
                    {
                        float along = i <= shaftSteps
                            ? shaftLen * i / shaftSteps
                            : shaftLen + headLen * (i - shaftSteps - 1) / headSteps;
                        float half = prof[i] * w;
                        poly[i] = new PointF(p0.X + dx * along - dy * half, p0.Y + dy * along + dx * half);
                        poly[poly.Length - 1 - i] = new PointF(p0.X + dx * along + dy * half, p0.Y + dy * along - dx * half);
                    }
                    g.FillPolygon(brush2, poly);
                }
            }            else if (a.Tool == "line" && pts.Count > 1)
            {
                using (var ink = new Pen(a.Color.IsEmpty ? Color.FromArgb(255, 59, 48) : a.Color, a.Width > 0 ? a.Width : 4))
                {
                    ink.StartCap = ink.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLine(ink, pts[0], pts[pts.Count - 1]);
                }
            }
            else if (a.Tool == "rect" && pts.Count > 1)
            {
                var r = Normalize(pts[0], pts[pts.Count - 1]);
                var rc = a.Color.IsEmpty ? Color.FromArgb(255, 59, 48) : a.Color;
                if (a.Filled)
                {
                    using (var fill = new SolidBrush(rc)) g.FillRectangle(fill, r);
                }
                else
                {
                    using (var ink = new Pen(rc, a.Width > 0 ? a.Width : 4))
                        g.DrawRectangle(ink, r);
                }
            }
            else if (a.Tool == "ellipse" && pts.Count > 1)
            {
                var r = Normalize(pts[0], pts[pts.Count - 1]);
                var ec = a.Color.IsEmpty ? Color.FromArgb(255, 59, 48) : a.Color;
                if (a.Filled)
                {
                    using (var fill = new SolidBrush(ec)) g.FillEllipse(fill, r);
                }
                else
                {
                    using (var ink = new Pen(ec, a.Width > 0 ? a.Width : 4))
                        g.DrawEllipse(ink, r);
                }
            }
            else if (a.Tool == "text" && pts.Count > 0)
            {
                int px = a.Width > 0 ? a.Width : 16;
                int wrapW = a.BoxW > 0 ? a.BoxW : 320;
                using (var f = new Font("Microsoft YaHei UI", px))
                using (var tb = new SolidBrush(a.Color.IsEmpty ? Color.FromArgb(255, 59, 48) : a.Color))
                    g.DrawString(a.Text ?? "", f, tb,
                        new RectangleF(pts[0].X, pts[0].Y, wrapW, 10000f));
            }
            else if (a.Tool == "number" && pts.Count > 0)
            {
                var p = pts[0];
                int rad = 16;
                g.FillEllipse(brush, p.X - rad, p.Y - rad, rad * 2, rad * 2);
                using (var f = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
                using (var w = new SolidBrush(Color.White))
                {
                    var s = a.Number.ToString();
                    var sz = g.MeasureString(s, f);
                    g.DrawString(s, f, w, p.X - sz.Width / 2, p.Y - sz.Height / 2);
                }
            }
        }
    }

    // ---- GDI rendering core -------------------------------------------------
    // The overlay paints straight into window DCs: a dimmed copy of the screen, the
    // bright selection area from the original, then the frame, handles and label.
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
        bool italic, bool underline, bool strike, int charset, int outPrec, int clipPrec, int quality, int pitch, string face);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string text, int len, ref RECT rc, int flags);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);

    const int SRCCOPY = 0x00CC0020, NULL_BRUSH = 5, NULL_PEN = 8;
    const int DT_LEFT = 0, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800;

    IntPtr hdcScreen, hdcDimmed, hdcBuf;
    IntPtr bmpScreen, bmpDim, bmpBuf, oldScreen, oldDim, oldBuf;
    IntPtr penFrame, penHair, brushAccent, brushWhite, fontLabel;
    Bitmap dimmed;

    // Timings, only used when DSH_SNIP_PERF is set (kept inert for release builds).
    static readonly bool Perf = System.Environment.GetEnvironmentVariable("DSH_SNIP_PERF") == "1";
    static long perfTicks, perfBlitTicks, perfFrameTicks, perfPixels;
    static int perfPaints, perfDirtyN, perfDirtyPx, perfRects, perfMaxW, perfMaxH;

    static int Rgb(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

    void InitGdi()
    {
        int dpi = 96;
        try { using (var g = CreateGraphics()) dpi = (int)Math.Round(g.DpiY); } catch (Exception) { }
        int height = -(int)Math.Round(11.0 * dpi / 72.0);
        penFrame = CreatePen(0, Math.Max(3, 3 * dpi / 96), Rgb(Program.Accent));
        penHair = CreatePen(0, Math.Max(1, dpi / 96), Rgb(Color.FromArgb(150, Program.Accent)));
        brushAccent = CreateSolidBrush(Rgb(Program.Accent));
        brushWhite = CreateSolidBrush(Rgb(Color.White));
        fontLabel = CreateFontW(height, 0, 0, 0, 700, false, false, false, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
    }

    void InitBlit()
    {
        if (screen == null) return;
        dimmed = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(dimmed))
        {
            g.DrawImage(screen, 0, 0);
            using (var b = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                g.FillRectangle(b, 0, 0, dimmed.Width, dimmed.Height);
        }

        hdcScreen = CreateCompatibleDC(IntPtr.Zero);
        bmpScreen = screen.GetHbitmap();
        oldScreen = SelectObject(hdcScreen, bmpScreen);

        hdcDimmed = CreateCompatibleDC(IntPtr.Zero);
        bmpDim = dimmed.GetHbitmap();
        oldDim = SelectObject(hdcDimmed, bmpDim);

        hdcBuf = CreateCompatibleDC(IntPtr.Zero);
        bmpBuf = CreateCompatibleBitmap(hdcDimmed, ClientSize.Width, ClientSize.Height);
        oldBuf = SelectObject(hdcBuf, bmpBuf);
        BitBlt(hdcBuf, 0, 0, ClientSize.Width, ClientSize.Height, hdcDimmed, 0, 0, SRCCOPY);
    }

    void DisposeGdi()
    {
        if (fontLabel != IntPtr.Zero) { DeleteObject(fontLabel); fontLabel = IntPtr.Zero; }
        if (brushWhite != IntPtr.Zero) { DeleteObject(brushWhite); brushWhite = IntPtr.Zero; }
        if (brushAccent != IntPtr.Zero) { DeleteObject(brushAccent); brushAccent = IntPtr.Zero; }
        if (penHair != IntPtr.Zero) { DeleteObject(penHair); penHair = IntPtr.Zero; }
        if (penFrame != IntPtr.Zero) { DeleteObject(penFrame); penFrame = IntPtr.Zero; }
    }

    void DisposeBlit()
    {
        if (hdcBuf != IntPtr.Zero) { SelectObject(hdcBuf, oldBuf); DeleteObject(bmpBuf); DeleteDC(hdcBuf); hdcBuf = IntPtr.Zero; }
        if (hdcDimmed != IntPtr.Zero) { SelectObject(hdcDimmed, oldDim); DeleteObject(bmpDim); DeleteDC(hdcDimmed); hdcDimmed = IntPtr.Zero; }
        if (hdcScreen != IntPtr.Zero) { SelectObject(hdcScreen, oldScreen); DeleteObject(bmpScreen); DeleteDC(hdcScreen); hdcScreen = IntPtr.Zero; }
        if (dimmed != null) { dimmed.Dispose(); dimmed = null; }
    }

    // Compose one dirty rectangle: dimmed screen everywhere, original inside the frame.
    void ComposeLayers(IntPtr hdc, Rectangle bright, Rectangle clip)
    {
        var inter = Rectangle.Intersect(clip, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        if (inter.Width <= 0 || inter.Height <= 0) return;
        BitBlt(hdc, inter.X, inter.Y, inter.Width, inter.Height, hdcDimmed, inter.X, inter.Y, SRCCOPY);
        var lit = Rectangle.Intersect(bright, inter);
        if (lit.Width > 0 && lit.Height > 0)
            BitBlt(hdc, lit.X, lit.Y, lit.Width, lit.Height, hdcScreen, lit.X, lit.Y, SRCCOPY);
    }

    void DrawSizeLabel(IntPtr hdc, Rectangle r)
    {
        if (fontLabel == IntPtr.Zero) return;
        string text = r.Width + " \u00d7 " + r.Height;
        int w = Math.Max(200, r.Width + 20);
        int y = r.Top - 40;
        if (y < 0) y = r.Top + 2;
        var rc = new RECT { Left = r.Left, Top = y, Right = r.Left + w, Bottom = y + 34 };
        var old = SelectObject(hdc, fontLabel);
        SetBkMode(hdc, 1);
        SetTextColor(hdc, 0x1A1A1A);
        var shadow = rc; shadow.Left += 2; shadow.Top += 2; shadow.Right += 2; shadow.Bottom += 2;
        DrawTextW(hdc, text, -1, ref shadow, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SetTextColor(hdc, 0xFFFFFF);
        DrawTextW(hdc, text, -1, ref rc, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(hdc, old);
    }

    // Green/blue selection frame: a thick border, eight square handles, the size label.
    void DrawGreenFrame(IntPtr hdc, Rectangle r)
    {
        if (r.Width < 2 || r.Height < 2) return;
        var oldPen = SelectObject(hdc, penFrame);
        var oldBrush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        GdiRect(hdc, r.Left - 3, r.Top - 3, r.Right + 3, r.Bottom + 3);
        SelectObject(hdc, oldBrush);

        SelectObject(hdc, GetStockObject(NULL_PEN));
        SelectObject(hdc, brushAccent);
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2, s = 6;
        foreach (var pt in new[]
        {
            new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom),
            new Point(cx, r.Top), new Point(r.Right, cy), new Point(cx, r.Bottom), new Point(r.Left, cy),
        })
            GdiRect(hdc, pt.X - s, pt.Y - s, pt.X + s, pt.Y + s);
        SelectObject(hdc, GetStockObject(NULL_BRUSH));

        DrawSizeLabel(hdc, r);
        SelectObject(hdc, oldPen);
    }

    [DllImport("gdi32.dll", EntryPoint = "Rectangle")] static extern bool GdiRect(IntPtr hdc, int l, int t, int r, int b);

    Rectangle LoupeBounds(Point p)
    {
        int panelW = 220, panelH = 306;
        int px = p.X + 20, py = p.Y + 46;
        if (px + panelW > ClientSize.Width) px = p.X - panelW - 20;
        if (py + panelH > ClientSize.Height) py = p.Y - panelH - 20;
        if (px < 0) px = 0;
        if (py < 0) py = 0;
        return new Rectangle(px, py, panelW, panelH);
    }

    void DrawAnnotations(IntPtr hdc, Rectangle area)
    {
        using (var bg = Graphics.FromHdc(hdc))
        {
            bg.SetClip(area);
            foreach (var a in annos)
            {
                if (a == editingAnno) continue;
                DrawAnno(bg, a);
            }
            if (draft != null) DrawAnno(bg, draft);
            if (Adjustable(activeAnno))
            {
                // Ellipse: a thin box joins the handles (the curve never reaches the
                // corner handles, so without it they look disconnected).
                if (activeAnno.Tool == "text")
                {
                    var tb = TextBounds(activeAnno);
                    if (tb.Width > 0)
                        using (var boxPen = new Pen(Program.Accent, 1.2f))
                            bg.DrawRectangle(boxPen, tb);
                }
                if (activeAnno.Tool == "ellipse")
                {
                    var box = Normalize(activeAnno.Points[0], activeAnno.Points[activeAnno.Points.Count - 1]);
                    using (var boxPen = new Pen(Color.FromArgb(190, Program.Accent), 1.2f))
                        bg.DrawRectangle(boxPen, box);
                }                foreach (var p in HandlesOf(activeAnno))
                {
                    using (var b = new SolidBrush(Color.White))
                        bg.FillEllipse(b, p.X - 6, p.Y - 6, 12, 12);
                    using (var pen = new Pen(Program.Accent, 2f))
                        bg.DrawEllipse(pen, p.X - 6, p.Y - 6, 12, 12);
                }
            }
            bg.ResetClip();
        }
    }

    void PaintCore(Graphics g, PaintEventArgs e)
    {
        var clip = e.ClipRectangle;
        if (clip.Width <= 0 || clip.Height <= 0) return;
        // A full invalidation (first paint, drag start) covers the client no matter
        // what the incremental rectangles say; trusting them there left stale chrome.
        bool full = clip.Width >= ClientSize.Width && clip.Height >= ClientSize.Height;
        var rects = (!full && pendingDirty.Count > 0) ? pendingDirty.ToArray() : new[] { clip };
        pendingDirty.Clear();
        // The probe counters were never incremented, so DSH_SNIP_PERF always reported zero.
        if (Perf)
        {
            perfPaints++;
            perfRects += rects.Length;
            foreach (var pr in rects)
            {
                perfPixels += (long)pr.Width * pr.Height;
                if (pr.Width > perfMaxW) perfMaxW = pr.Width;
                if (pr.Height > perfMaxH) perfMaxH = pr.Height;
            }
            perfTicks += 0;
        }

        bool dragging = dragMode == 1 && sel.Width > 1 && sel.Height > 1;
        bool noFrame = !hasSel || sel.Width < 2 || sel.Height < 2;
        Rectangle bright;
        if (dragging) bright = sel;
        else if (noFrame) bright = (dragMode == 0 && hasHover && hoverRect.Width > 0) ? hoverRect : Rectangle.Empty;
        else bright = sel;

        // Compose into the offscreen DC along the changed rectangles only, then blit
        // exactly those. Using the bounding box made the bottom-right handle (the only
        // one that grows both axes) cost as much as the whole frame every frame.
        var swB = Perf ? System.Diagnostics.Stopwatch.StartNew() : null;
        foreach (var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            ComposeLayers(hdcBuf, bright, r);
        }
        if (swB != null) { swB.Stop(); perfBlitTicks += swB.ElapsedTicks; }

        var swF = Perf ? System.Diagnostics.Stopwatch.StartNew() : null;
        if (dragging)
        {
            DrawGreenFrame(hdcBuf, sel);
        }
        else if (noFrame)
        {
            if (bright.Width > 0) DrawGreenFrame(hdcBuf, bright);
        }
        else
        {
            DrawAnnotations(hdcBuf, sel);
            // Dashed outline around the open text box, like WeChat: the box itself is
            // borderless and blends into the screenshot, so it needs this hint.

            DrawGreenFrame(hdcBuf, sel);
        }
        if (swF != null) { swF.Stop(); perfFrameTicks += swF.ElapsedTicks; }

        var hdc = g.GetHdc();
        try
        {
            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                BitBlt(hdc, r.X, r.Y, r.Width, r.Height, hdcBuf, r.X, r.Y, 0x00CC0020);
            }
        }
        finally { g.ReleaseHdc(hdc); }
    }

    // The overlay is painted by hand: dim + bright area + chrome, composed off-screen
    // and blitted once (this override was lost when the rendering core was restored).
    protected override void WndProc(ref Message m)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        if (m.Msg == 0x007B) return;                     // WM_CONTEXTMENU: no menu
        // Consume the whole right-click here: closing on the button-down handed the
        // button-up to the desktop, which is what opened its context menu.
        if (m.Msg == 0x0204) return;                      // WM_RBUTTONDOWN: swallow
        if (m.Msg == 0x0205) { Close(); return; }          // WM_RBUTTONUP: this is the cancel
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (IsDisposed || Disposing) return;   // messages can still arrive after Dispose
        if (screen == null || hdcBuf == IntPtr.Zero)
        {
            using (var b = new SolidBrush(Color.Black)) e.Graphics.FillRectangle(b, ClientRectangle);
            return;
        }
        PaintCore(e.Graphics, e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
}

// Toolbar drawn as one control: rounded surface, themed colours, hover and selected
// chips, vector icons. A FlowLayoutPanel of Buttons cannot do any of that.
// TextBox that tells the overlay when the IME starts composing, so the candidate
// window can be positioned before Windows shows it.
// Stand-alone text editor window: a normal, activatable top-level window, so the IME
// attaches to it and draws its composition and candidate windows next to the caret.
public class TextEditorForm : Form
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll")] static extern bool ImmNotifyIME(IntPtr hIMC, int action, int index, int value);

    public readonly ImeTextBox Box;
    readonly Color tint;
    // How wide the box may get before it wraps; 0 falls back to the screen edge.
    readonly int boxMaxWidth;
    // Screen rectangle the window has to stay inside vertically (Empty = no limit).
    readonly Rectangle keepInside;
    string fittedText;   // last text that still fitted; extra input is trimmed back to it
    bool reverting;
    bool noRoom;         // one more character would no longer fit in the frame
    public Action<string> Done;
    public Action Cancelled;
    // Wrap width of the last layout. Kept as a plain field because the window may be
    // disposed by the time the commit is handled, which made ClientSize unusable.
    public int WrapWidth;
    // Where the box ended up (screen coords). Finish() records it before the window
    // is closed, because the text must be drawn where the user saw it.
    public Point ResultLocation;

    public TextEditorForm(Font font, Color fore, Color back, int maxWidth, Rectangle inside)
    {
        tint = back;
        boxMaxWidth = maxWidth;
        keepInside = inside;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = back;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        Box = new ImeTextBox
        {
            // Wraps only at boxMaxWidth (the frame's right edge), so a long string
            // stays inside the selection instead of running off the screen.
            Multiline = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            Font = font,
            ForeColor = fore,
            BackColor = back,
            AcceptsReturn = false,
        };
        Controls.Add(Box);
        Box.Bounds = new Rectangle(4, 2, 132, font.Height + 6);
        Box.KeyDown += (s, e) =>
        {
            // Enter makes a new line (the box is multiline); Ctrl+Enter commits, and
            // clicking elsewhere still commits too.
            if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Finish(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Cancel(); }
        };
        Box.TextChanged += (s, e) => Layout();
        // The IME keeps the string being typed to itself; this fires while it does.
        Box.CompositionChanged = Layout;
        Box.Blocked = () => noRoom;
        Layout();
    }

    void Layout()
    {
        // IME composition never reaches TextBox.Text until it is committed, so the
        // box used to stay at its initial width while the user typed: the string
        // wrapped after three characters and spilled out of the dashed outline.
        string shown = Box.Text + Box.Composition;
        // 8px past the caret: the control's own edges take ~4px, so the visible gap stays
        // small. The earlier wrap happened because the height measure below used w - 8.
        int need = TextRenderer.MeasureText(shown.Length == 0 ? " " : shown, Box.Font,
            new Size(10000, 1000), TextFormatFlags.NoPadding).Width + 2;
        int maxW = boxMaxWidth > 0
            ? Math.Max(140, boxMaxWidth)
            : Math.Max(140, Screen.FromPoint(Location).Bounds.Width - Left - 40);
        // Never narrower than one comfortable line: measuring an empty box gave ~30px.
        int w = Math.Max(60, Math.Min(need, maxW));
        WrapWidth = w;
        if (Box.Width != w) Box.Width = w;

        int cap = keepInside.Height > 0 ? keepInside.Height : int.MaxValue;
        // When the text no longer fits, the extra characters are dropped instead of being
        // scrolled out of sight: whatever the user can see stays where it is. The box is
        // never made read-only and the input method is never touched.
        bool fits = HeightFor(Box.Text) <= cap;
        // A line only wraps once the current one is full, so "no room" means one more
        // character would need a line the frame cannot hold. Decided from the committed
        // text only: a temporary composition must not flip the box into this state.
        noRoom = HeightFor(Box.Text + "\u6d4b") > cap;
        if (!fits && !reverting && fittedText != null && fittedText.Length > 0
            && Box.Text.Length > fittedText.Length && Box.Text.StartsWith(fittedText))
        {
            reverting = true;
            try
            {
                Box.Text = fittedText;
                Box.SelectionStart = Box.TextLength;
                Box.SelectionLength = 0;
            }
            finally { reverting = false; }
            return;
        }
        if (fits) fittedText = Box.Text;

        // Count the lines the control itself laid out: the wrap-aware measurement added
        // its own per-line padding, so every Enter grew the box far more than one line.
        int lines = Math.Max(1, Box.GetLineFromCharIndex(Box.TextLength) + 1);
        if (Box.Composition.Length > 0) lines += 1;
        int single = HeightFor("\u554a");
        int h = Math.Min(single + (lines - 1) * (Box.Font.Height + 2), cap);
        if (Box.Height != h) Box.Height = h;
        ClientSize = new Size(w + 8, Box.Height + 4);
        // Only a note that was already too long (re-opened in a smaller frame) needs the
        // caret scrolled into view; live typing is trimmed back above and never scrolls.
        if (!fits) SendMessage(Box.Handle, 0x00B7, IntPtr.Zero, IntPtr.Zero); // EM_SCROLLCARET
        // Reaching the frame's bottom edge must not push the box outside it: move it
        // up by as many lines as needed (WeChat does the same), never above the top.
        if (keepInside.Height > 0 && Bottom > keepInside.Bottom)
        {
            int top = Math.Max(keepInside.Top, keepInside.Bottom - Height);
            if (Top != top) Top = top;
        }
        Invalidate();
    }

    // Height the text needs at the box's current width.
    int HeightFor(string text)
    {
        if (string.IsNullOrEmpty(text)) return Box.Font.Height + 8;
        var sz = TextRenderer.MeasureText(text, Box.Font,
            new Size(Math.Max(80, Box.Width), 4000), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return Math.Max(Box.Font.Height + 8, sz.Height + 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var b = new SolidBrush(tint)) e.Graphics.FillRectangle(b, ClientRectangle);
        using (var pen = new Pen(Color.FromArgb(235, 128, 128, 128), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Layout();
        Activate();
        Box.Focus();
    }

    // No OnDeactivate hook: the overlay re-activates on every mouse-up, which would
    // close the editor immediately. Committing is explicit (Enter or a click outside).
    public void CommitText()
    {
        // Finish the IME composition first: closing the box while it was composing threw
        // the characters away and the note came out empty.
        try
        {
            if (Box.IsHandleCreated)
            {
                IntPtr himc = ImmGetContext(Box.Handle);
                if (himc != IntPtr.Zero)
                {
                    ImmNotifyIME(himc, 0x0005, 0x0001, 0);   // NI_COMPOSITIONSTR, CPS_COMPLETE
                    ImmReleaseContext(Box.Handle, himc);
                }
            }
        }
        catch (Exception) { }
        if (!IsDisposed && Visible) Finish();
    }

    void Finish()
    {
        Program.TLog("editor Finish, Visible=" + Visible);
        // The width may still be the one measured before the last composition landed;
        // laying out once more gives the annotation the wrap width the user actually saw.
        Layout();
        ResultLocation = Location;
        var text = Box.Text;
        var handler = Done;
        Visible = false;
        Close();
        if (handler != null) handler(text);
    }

    void Cancel()
    {
        Program.TLog("editor Cancel");
        var handler = Cancelled;
        Visible = false;
        Close();
        if (handler != null) handler();
    }
}

// A TextBox that says when the IME composition changed and what it contains.
// Windows draws the composition inside the control without putting it in the text
// buffer, so neither TextChanged nor Text knows about it.
public class ImeTextBox : TextBox
{
    public Action CompositionChanged;
    // Asked before a character is inserted: returning true drops it outright, which is
    // what keeps a full box from flashing the character and then eating it again.
    public Func<bool> Blocked;
    bool inNotify;

    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll")] static extern int ImmGetCompositionStringW(IntPtr hIMC, int index, byte[] buf, int len);

    const int GCS_COMPSTR = 0x0008;

    public string Composition
    {
        get
        {
            try
            {
                IntPtr hIMC = ImmGetContext(Handle);
                if (hIMC == IntPtr.Zero) return "";
                try
                {
                    int bytes = ImmGetCompositionStringW(hIMC, GCS_COMPSTR, null, 0);
                    if (bytes <= 1) return "";
                    var buf = new byte[bytes];
                    int n = ImmGetCompositionStringW(hIMC, GCS_COMPSTR, buf, bytes);
                    if (n <= 0) return "";
                    return Encoding.Unicode.GetString(buf, 0, n);
                }
                finally { ImmReleaseContext(Handle, hIMC); }
            }
            catch (Exception) { return ""; }
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (Blocked != null && Blocked())
        {
            // Printable characters only: WM_CHAR also carries Backspace (0x08), and
            // dropping that would leave the user unable to free space again.
            if (m.Msg == 0x0102 && (long)m.WParam >= 0x20) return;
            if (m.Msg == 0x0302) return;                                      // WM_PASTE
            if (m.Msg == 0x010F && (((long)m.LParam) & 0x0800) != 0) return;  // IME result string
        }
        base.WndProc(ref m);
        if (m.Msg != 0x010D && m.Msg != 0x010F && m.Msg != 0x010E) return;
        if (inNotify) return;
        inNotify = true;
        try { var h = CompositionChanged; if (h != null) h(); }
        catch (Exception) { }
        finally { inNotify = false; }
    }
}

public class SnipTextBox : TextBox
{
    public Action ImeStarted;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x010D || m.Msg == 0x010F) // WM_IME_STARTCOMPOSITION / WM_IME_COMPOSITION
        {
            if (ImeStarted != null) { try { ImeStarted(); } catch (Exception) { } }
        }
        base.WndProc(ref m);
    }
}

public class ToolBarStrip : Control
{
    // Inside a layered window this control never receives WM_PAINT, so every repaint
    // request has to tell the window to redraw its bitmap instead.
    public Action Changed;

    public new void Invalidate()
    {
        base.Invalidate();
        if (Changed != null) Changed();
    }
    public class Item
    {
        public string Name;
        public string Tip;
        public Action Click;
        public Action<Graphics, Rectangle, Color> Paint;
    }

    readonly List<Item> items = new List<Item>();
    readonly ToolTip tips = new ToolTip();
    int hover = -1;

    string selected;
    public string Selected
    {
        get { return selected; }
        set { if (selected == value) return; selected = value; Invalidate(); }
    }
    public Action<bool> Unused_hook;
    public Func<bool> CanUndo;
    public Size NaturalSize;
    public Color Back, Border, Fore, Accent, Hover, Chip, Divider;

    const int ItemSize = 54, Gap = 4, PadX = 12, PadY = 8;
    // One divider between the tools and the actions (after the text item).
    const int SepAfter = 5, SepGap = 20;

    public ToolBarStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        bool dark = Program.DarkMode;
        Back = dark ? Color.FromArgb(40, 40, 40) : Color.White;
        Border = dark ? Color.FromArgb(78, 78, 78) : Color.FromArgb(186, 186, 186);
        Fore = dark ? Color.FromArgb(226, 226, 226) : Color.FromArgb(58, 62, 68);
        Accent = Program.Accent;
        Hover = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(240, 240, 240);
        Chip = dark ? Color.FromArgb(46, 74, 128) : Color.FromArgb(228, 238, 252);
        Divider = dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(224, 224, 224);
    }

    public void Add(string name, string tip, Action click, Action<Graphics, Rectangle, Color> paint)
    {
        items.Add(new Item { Name = name, Tip = tip, Click = click, Paint = paint });
        Width = PadX * 2 + items.Count * (ItemSize + Gap) - Gap + (items.Count > SepAfter ? SepGap : 0);
        Height = PadY * 2 + ItemSize;
        NaturalSize = Size;
        RefreshRegion();
    }

    public void RefreshRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using (var path = Rounded(new Rectangle(0, 0, Width, Height), 12))
        {
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- pen options panel -------------------------------------------------
    public bool ShowOptions;
    int penWidth = 5;
    public int PenWidth
    {
        get { return penWidth; }
        set { if (penWidth == value) return; penWidth = value; Invalidate(); }
    }
    Color penColor = Color.FromArgb(255, 59, 48);
    public Color PenColor
    {
        get { return penColor; }
        set { if (penColor.ToArgb() == value.ToArgb()) return; penColor = value; Invalidate(); }
    }
    bool penFill;
    public bool PenFill
    {
        get { return penFill; }
        set { if (penFill == value) return; penFill = value; Invalidate(); }
    }
    bool showFill;
    // Only closed shapes offer the fill option. Changing this has to repaint at once:
    // SetOptions early-returns when the panel stays open, so nothing else did it.
    public bool ShowFill
    {
        get { return showFill; }
        set { if (showFill == value) return; showFill = value; Invalidate(); }
    }
    public Action PenChanged;
    public const int OptionHeight = 56;
    static readonly int[] PenSizes = { 2, 5, 10 };
    // The fourth chip is "solid": same row as the three widths, not a separate button.
    const int SolidIndex = 3;
    static readonly Color[] Inks =
    {
        Color.FromArgb(30, 144, 255), Color.FromArgb(0, 190, 120), Color.FromArgb(245, 200, 30),
        Color.FromArgb(128, 128, 128), Color.White, Color.FromArgb(255, 59, 48),
    };

    int BaseHeight { get { return PadY * 2 + ItemSize; } }


    public void SetOptions(bool on)
    {
        if (ShowOptions == on) return;
        ShowOptions = on;
        Height = BaseHeight + (on ? OptionHeight : 0);
        NaturalSize = new Size(Width, Height);
        RefreshRegion();
        Invalidate();
    }

    RectangleF DotRect(int i)
    {
        float cy = BaseHeight + OptionHeight / 2f;
        if (i == SolidIndex)
        {
            float scx = PadX + 16 + SolidIndex * 30;
            return new RectangleF(scx - 11f, cy - 11f, 22f, 22f);
        }
        // Drawn a little larger than the actual stroke width so the three are readable.
        float d = PenSizes[i] + 4f, cx = PadX + 16 + i * 30;
        return new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
    }

    Rectangle SwatchRect(int i)
    {
        return new Rectangle(SwatchX + i * 28, BaseHeight + (OptionHeight - 22) / 2, 22, 22);
    }

    void PaintOptions(Graphics g)
    {
        for (int i = 0; i < PenSizes.Length; i++)
        {
            var dot = DotRect(i);
            using (var b = new SolidBrush(Fore)) g.FillEllipse(b, dot);
            if (PenWidth == PenSizes[i] && !PenFill)
                using (var pen = new Pen(Accent, 1.6f)) g.DrawEllipse(pen, dot.X - 5, dot.Y - 5, dot.Width + 10, dot.Height + 10);
        }

        if (ShowFill)
        {
            var solid = DotRect(SolidIndex);
            // Always the same icon: a square with a solid core (that is the label, not the state).
            using (var path = Rounded(solid, 5))
            using (var pen = new Pen(Fore, 1.6f)) g.DrawPath(pen, path);
            using (var inner = Rounded(new Rectangle((int)solid.X + 4, (int)solid.Y + 4, (int)solid.Width - 8, (int)solid.Height - 8), 2))
            using (var b = new SolidBrush(Fore)) g.FillPath(b, inner);
            if (PenFill)
                using (var pen = new Pen(Accent, 1.8f))
                    using (var path = Rounded(new Rectangle((int)solid.X - 4, (int)solid.Y - 4, (int)solid.Width + 8, (int)solid.Height + 8), 7))
                        g.DrawPath(pen, path);
        }


    float dx = DividerX;
        using (var pen = new Pen(Border)) g.DrawLine(pen, dx, BaseHeight + 12, dx, BaseHeight + OptionHeight - 12);
        for (int i = 0; i < Inks.Length; i++)
        {
            var r = SwatchRect(i);
            using (var path = Rounded(r, 5))
            using (var b = new SolidBrush(Inks[i]))
                g.FillPath(b, path);
            using (var path = Rounded(r, 5))
            using (var pen = new Pen(Program.DarkMode ? Color.FromArgb(70, 70, 70) : Color.FromArgb(210, 210, 210)))
                g.DrawPath(pen, path);
            if (PenColor.ToArgb() == Inks[i].ToArgb())
                using (var path = Rounded(new Rectangle(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6), 7))
                using (var pen = new Pen(Accent, 2f))
                    g.DrawPath(pen, path);
        }
    }

    // Fourth option: solid fill, sitting between the sizes and the colours.
    Rectangle FillRect()
    {
        return new Rectangle(PadX + 88, BaseHeight + (OptionHeight - 22) / 2, 22, 22);
    }

    // Centred: the same gap on the chip side and on the swatch side.
    float DividerX { get { return PadX + (ShowFill ? 143 : 105); } }
    int SwatchX { get { return PadX + (ShowFill ? 169 : 129); } }

    Rectangle Box(int i)
    {
        return new Rectangle(PadX + i * (ItemSize + Gap) + (i > SepAfter ? SepGap : 0), PadY, ItemSize, ItemSize);
    }

    int IndexAt(Point p)
    {
        for (int i = 0; i < items.Count; i++) if (Box(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintContent(e.Graphics);
    }

    // Drawn either into the control or into the layered window's bitmap.
    public void PaintContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Icons.UndoEnabled = CanUndo == null || CanUndo();
        using (var path = Rounded(new Rectangle(0, 0, Width, Height), 12))
        using (var b = new SolidBrush(Back))
            g.FillPath(b, path);

        if (items.Count > SepAfter + 1)
        {
            int sepX = PadX + (SepAfter + 1) * (ItemSize + Gap) - Gap + SepGap / 2;
            using (var pen = new Pen(Divider, 1.4f))
                g.DrawLine(pen, sepX, PadY + 11, sepX, PadY + ItemSize - 11);
        }

        for (int i = 0; i < items.Count; i++)
        {
            var box = Box(i);
            bool selected = items[i].Name == Selected;
            if (selected || i == hover)
            {
                using (var path = Rounded(box, 8))
                using (var b = new SolidBrush(selected ? Chip : Hover))
                    g.FillPath(b, path);
            }
            items[i].Paint(g, box, selected ? Accent : Fore);
        }

        if (ShowOptions) PaintOptions(g);

        // Inset by a pixel: on the edge the layered alpha falloff swallowed the line.
        using (var path = Rounded(new RectangleF(0.8f, 0.8f, Width - 1.6f, Height - 1.6f), 11.5f))
        using (var pen = new Pen(Border, 1.6f))
            g.DrawPath(pen, path);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i == hover) return;
        hover = i;
        tips.SetToolTip(this, i >= 0 ? items[i].Tip : "");
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        tips.SetToolTip(this, "");
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (ShowOptions && e.Y >= BaseHeight)
        {
            if (ShowFill)
            {
                var solidHit = RectangleF.Inflate(DotRect(SolidIndex), 8, 8);
                if (solidHit.Contains(e.Location))
                {
                    // Same logic as the widths: it is one of the four choices, not a toggle.
                    PenFill = true;
                    Invalidate();
                    if (PenChanged != null) PenChanged();
                    return;
                }
            }
            for (int i = 0; i < PenSizes.Length; i++)
            {
                var dot = DotRect(i);
                var hit = RectangleF.Inflate(dot, 12, 12);
                if (hit.Contains(e.Location))
                {
                    PenWidth = PenSizes[i];
                    PenFill = false;
                    Invalidate();
                    if (PenChanged != null) PenChanged();
                    return;
                }
            }
            for (int i = 0; i < Inks.Length; i++)
            {
                if (SwatchRect(i).Contains(e.Location))
                {
                    PenColor = Inks[i];
                    Invalidate();
                    if (PenChanged != null) PenChanged();
                    return;
                }
            }
            return;
        }
        int idx = IndexAt(e.Location);
        if (idx >= 0 && items[idx].Click != null) items[idx].Click();
    }
}

// The toolbar lives in its own top-level window: as a child control its rectangle
// was cut out of the overlay's paint region, so hiding it left stale pixels behind.
public class ToolBarForm : Form
{
    // The bar is drawn into a 32bpp bitmap and handed to the window with per-pixel
    // alpha; a window Region can only clip hard, which left stair-stepped corners.
    [StructLayout(LayoutKind.Sequential)] struct LPOINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct LSIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNC
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref LPOINT pptDst, ref LSIZE psize, IntPtr hdcSrc, ref LPOINT pptSrc, int crKey,
        ref BLENDFUNC pblend, int dwFlags);

    Bitmap layeredBmp;
    bool layeredDirty = true;
    Size layeredSize;

    public void ApplyLayered()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        var __sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (layeredBmp == null || layeredSize.Width != Width || layeredSize.Height != Height)
            {
                if (layeredBmp != null) layeredBmp.Dispose();
                layeredBmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                layeredSize = new Size(Width, Height);
                layeredDirty = true;
            }
            var bmp = layeredBmp;
            if (layeredDirty)
            {
                layeredDirty = false;
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Strip.PaintContent(g);
                }
            }
            {
                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr old = SelectObject(hdcMem, hbmp);
                var dst = new LPOINT { X = Left, Y = Top };
                var src = new LPOINT();
                var size = new LSIZE { cx = Width, cy = Height };
                var blend = new BLENDFUNC { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, hdcScreen, ref dst, ref size, hdcMem, ref src, 0, ref blend, 2);
                SelectObject(hdcMem, old);
                DeleteObject(hbmp);
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
        catch (Exception) { }
        __sw.Stop();
        if (__sw.ElapsedMilliseconds >= 5) Program.TLog("layered render " + __sw.ElapsedMilliseconds + "ms " + Width + "x" + Height);
    }

    public readonly ToolBarStrip Strip;



    public ToolBarForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(40, 40, 40);
        Strip = new ToolBarStrip();
        Controls.Add(Strip);
        // Any strip repaint (hover, selection, options row) redraws the layered bitmap.
        Strip.Changed = () => { layeredDirty = true; ApplyLayered(); };
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        ApplyLayered();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyLayered();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED: per-pixel alpha, smooth corners
            return cp;
        }
    }

    public void Add(string name, string tip, Action click, Action<Graphics, Rectangle, Color> paint)
    {
        Strip.Add(name, tip, click, paint);
        // Docking would clamp the strip to the still-empty form; size both explicitly.
        Strip.Bounds = new Rectangle(0, 0, Strip.NaturalSize.Width, Strip.NaturalSize.Height);
        ClientSize = Strip.NaturalSize;
        RefreshRegion();
    }

    public string Selected
    {
        get { return Strip.Selected; }
        set { Strip.Selected = value; Strip.Invalidate(); }
    }


    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var owner = Owner as SnipForm;
        if (owner != null && owner.HandleShortcut(keyData & Keys.KeyCode, (keyData & Keys.Control) == Keys.Control))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SetOptions(bool on)
    {
        if (Strip.ShowOptions == on) return;
        Strip.SetOptions(on);
        Strip.Bounds = new Rectangle(0, 0, Strip.NaturalSize.Width, Strip.NaturalSize.Height);
        ClientSize = Strip.NaturalSize;
        RefreshRegion();
    }

    public void RefreshRegion()
    {
        // Deliberately empty: the shape comes from the layered bitmap's alpha channel.
        if (true) return;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        // Same radius as the strip paints: a different one exposed the form background
        // as dark specks in the four corners.
        using (var path = ToolBarStrip.Rounded(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 12))
        {
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }
    }
}

public static class Icons
{
    static Pen Stroke(Color c)
    {
        var p = new Pen(c, 1.5f);
        p.StartCap = LineCap.Round;
        p.EndCap = LineCap.Round;
        p.LineJoin = LineJoin.Round;
        return p;
    }

    delegate void Draw(Graphics g, Color c);

    static Action<Graphics, Rectangle, Color> Icon(Draw draw)
    {
        return (g, box, color) =>
        {
            var state = g.Save();
            // Painters use a 22-unit grid; scale it up so the bigger bar keeps the
            // same proportions and every existing icon grows with it.
            float canvas = Math.Max(26f, box.Height * 0.70f);
        const float unit = 22f;
            g.TranslateTransform(box.Left + (box.Width - canvas) / 2f, box.Top + (box.Height - canvas) / 2f);
            g.ScaleTransform(canvas / unit, canvas / unit);
            draw(g, color);
            g.Restore(state);
        };
    }

    public static readonly Action<Graphics, Rectangle, Color> Cursor = Icon((g, c) =>
    {
        using (var p = Stroke(c))
        using (var b = new SolidBrush(c))
        {
            var pts = new[] { new PointF(6, 3), new PointF(6, 17), new PointF(9.6f, 13.4f), new PointF(12.2f, 18.6f), new PointF(14.4f, 17.6f), new PointF(11.9f, 12.6f), new PointF(16.4f, 12.6f) };
            g.FillPolygon(b, pts);
            g.DrawPolygon(p, pts);
        }
    });

    // The brush glyph is traced from the reference artwork (64x64 alpha mask, PNG,
    // base64) and tinted at draw time, so it looks exactly like the original at any
    // size while still following the light/dark theme.
    public static class Glyph
    {
        const string BrushMaskB64 = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAAAAACPAi4CAAAEcklEQVR42p1Xb2iWVRT/3Xuf122ubYm6qZUZiyyJ/nyJoATBpCAookA/WImwYJVr/lk2LcMCwz6E+kGIEkOjpL6E5DZFctpqpbOGBJWFQbRszEAEX7c955xfH+77OiFq99398vI87/M753fO79xz7nWofDnnABqmukLJjI8/FbsHcdut04aHfoefCgnv8dSxMZIjnyyEnwIe1XtIqpK82FKOppLw645QxEzNcnJVpRw8arqZk0JSKTbUVJkFj+t6mJPG0SGhUfhGRUF4FLqZ05Q77qp/8CxVrL8SFT1qP2NOE64HgEfHVDlUX0n+qj6P+I3IfMhuuUTlxbmV5K8r4l9GcMjwJE05PDPdf213xG9FADwaB6lqZ0Jl+pmwA8HBo7GfSuHriSpM+N8c8Q1fUah2/maXpIIPNaX8bUHm4NFwmDnNuDytjjyqr+UfMLufQlNrTQsgoK5UP1sj/7oT0X8bssT66Yn4V6L/eb0UmsqaNP8e9V9EfCsKQEDDAIWmbEvF1xyJ8a+N/md2R/6bU/nXl/i3R/ycU9H/OiQJ6DH9cPTficwhYNbxaG4TQiK+xL8dAQho+jbqtwbeJelXXdJvc4l/X1m/VP9dEd8J7xAw9xvmNLV1afjy/tdy/c0ZiI/Pp+Gdm34wAl6Fd/CYNxj5d6TF73xtF3MaY8ABs78rl1NI24DYyXGaFjfE/NcfjfjX0vwDDmdEqPy5AQEBTV9G/EsoJOGdL/gXKUZl/w0oYMbXMf52+BQCPnaJTVSj8kwzboz6aXtS/MEBja1vPwJspBqFZ5efiPyfS8F7B9z//p8k1wMbIgdSr7aDyWIPwOM9Skqu3Aq00oxqShNuSMED9/WQFDWachvQZmokzdiZsP89Ch1XqGKiVJpwT8CzNKVqvjZBf4+6Q6RQjRylkTl3Z1itHCdXI0vgP+Mgc6OSvc8s2kshc+6dhpVXeGVVQv9zvv5YVGvgYQDYzZzMua8KS7csTqnfgHc4TjNuKwA+eL8vcujwEyfD/0/A4jExU2mJnzuX7aBS9BCyzKfs/qpBCpUt5Ww5hwOUnAcmG3/xb89ld1tQ/+F7mRIAwOAGQeDH1Pm533Kz4ZucnyDVTTEuSxpADg3nqDm3T3zt3cIilX83Ia0BP2BmdnnRBIGAXRSxI2nnuIDlFOHJa9/cUTSThBHqYwi3g8QPV792LOyqIf3QR7AUA8QCAPipzNd52/6Qerr9FwLTWmgfRbmixCAAb1Ko9sd859NS0PQXhZfmwwEuZKh5N9bxisQmXoWVVLE+533IACw5TSVz7nEh8RbjT1OVT0drS/bmFHKcn2YhcYbgLary1KzGxubHdnwfm6iwa3paAhyu30klOfzL+ZEiSYqZkB/UJt5GXFUvheWluZoJefmF9BvhjKKNxz6sZqYi5OWP70kdooBD5xhJqoiokeTIzntD+mXKAbjziaXNc2PGR3892Xf8HDxZgQFvwMw5CxoyK45c+G0U8JNugH8d591/PCSGALh4pQeZzr20/gEiuE0GJPqGZAAAAABJRU5ErkJggg==";
        static Bitmap mask;
        static readonly System.Collections.Generic.Dictionary<int, Bitmap> tinted =
            new System.Collections.Generic.Dictionary<int, Bitmap>();

        static Bitmap Mask()
        {
            if (mask != null) return mask;
            using (var ms = new System.IO.MemoryStream(Convert.FromBase64String(BrushMaskB64)))
            using (var src = new Bitmap(ms))
            {
                mask = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(mask)) g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return mask;
        }

        static Bitmap Tinted(Color color)
        {
            int key = color.ToArgb();
            Bitmap cached;
            if (tinted.TryGetValue(key, out cached)) return cached;
            var src = Mask();
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    int a = src.GetPixel(x, y).R;   // the mask is grayscale: luminance is the alpha
                    bmp.SetPixel(x, y, Color.FromArgb(a, color.R, color.G, color.B));
                }
            tinted[key] = bmp;
            return bmp;
        }

        public static void Paint(Graphics g, RectangleF box, Color color) { Draw(g, box, color); }

        public static void Draw(Graphics g, RectangleF box, Color color)
        {
            var state = g.Save();
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(Tinted(color), box);
            g.Restore(state);
        }
    }
    // Flat pencil: wide body, long taper to the point, cap band near the top.
    public static readonly Action<Graphics, Rectangle, Color> Pen = Icon((g, c) =>
    {
        var state = g.Save();
        g.TranslateTransform(11f, 12.2f);   // the pencil's ink sits high otherwise
        g.RotateTransform(45f);    // +45 puts the tip at the lower left
        g.ScaleTransform(1.08f, 1.08f);
        using (var pen = new Pen(c, 1.7f))
        {
            pen.LineJoin = LineJoin.Round;
            pen.StartCap = pen.EndCap = LineCap.Round;
            using (var body = new GraphicsPath())
            {
                body.AddLine(-2.6f, 2.6f, 0f, 8.4f);
                body.AddLine(0f, 8.4f, 2.6f, 2.6f);
                body.AddLine(2.6f, 2.6f, 2.6f, -8.6f);
                body.AddArc(-2.6f, -10.8f, 5.2f, 4.4f, 0f, -180f);   // over the top
                body.AddLine(-2.6f, -8.6f, -2.6f, 2.6f);
                body.CloseFigure();
                g.DrawPath(pen, body);
            }
            g.DrawLine(pen, -2.6f, 2.6f, 2.6f, 2.6f);
            g.DrawLine(pen, -2.6f, -7.2f, 2.6f, -7.2f);
        }
        g.Restore(state);
    });

    public static readonly Action<Graphics, Rectangle, Color> Line = Icon((g, c) =>
    {
        using (var p = Stroke(c))
        {
            p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawLine(p, 3f, 18.5f, 18.5f, 3.5f);
        }
    });

    // Outline arrow: a diagonal with two short legs forming the corner, matching the
    // reference icon rather than a filled head.
    public static readonly Action<Graphics, Rectangle, Color> Arrow = Icon((g, c) =>
    {
        // One path, not three lines: separate round-capped lines pile three caps on
        // the corner and one of them pokes out past it.
        using (var path = new GraphicsPath())
        using (var p = new Pen(c, 1.9f))
        {
            path.AddLine(3.5f, 18.5f, 18.5f, 3.5f);
            path.AddLine(18.5f, 3.5f, 8.5f, 3.5f);
            path.StartFigure();
            path.AddLine(18.5f, 3.5f, 18.5f, 13.5f);
            p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            g.DrawPath(p, path);
        }
    });

    // Rounded square, matching the reference icon (thick outline, soft corners).
    public static readonly Action<Graphics, Rectangle, Color> Rect = Icon((g, c) =>
    {
        using (var p = new Pen(c, 1.8f))
        using (var path = ToolBarStrip.Rounded(new RectangleF(3.2f, 3.2f, 15.6f, 15.6f), 3.4f))
            g.DrawPath(p, path);
    });

    // Matches the rectangle icon: same footprint, same stroke, just round.
    public static readonly Action<Graphics, Rectangle, Color> Ellipse = Icon((g, c) =>
    {
        using (var p = new Pen(c, 1.8f))
            g.DrawEllipse(p, 3.2f, 3.2f, 15.6f, 15.6f);
    });

    public static readonly Action<Graphics, Rectangle, Color> Mosaic = Icon((g, c) =>
    {
        using (var b = new SolidBrush(c))
        {
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    if ((row + col) % 2 == 0) g.FillRectangle(b, 4.5f + col * 4.4f, 6.5f + row * 3.4f, 4f, 3f);
        }
    });

    public static readonly Action<Graphics, Rectangle, Color> Text = Icon((g, c) =>
    {
        using (var frame = new Pen(c, 1.7f))
        using (var path = ToolBarStrip.Rounded(new RectangleF(3.4f, 3.4f, 15.2f, 15.2f), 2.4f))
            g.DrawPath(frame, path);
        using (var p = new Pen(c, 1.7f))
        {
            p.StartCap = p.EndCap = LineCap.Round;
            g.DrawLine(p, 7.6f, 7.6f, 14.4f, 7.6f);
            g.DrawLine(p, 11f, 7.6f, 11f, 15f);
        }
    });

    public static readonly Action<Graphics, Rectangle, Color> Save = Icon((g, c) =>
    {
        var st = g.Save();
        g.TranslateTransform(11f, 10.4f);
        g.ScaleTransform(1.2f, 1.2f);
        g.TranslateTransform(-11f, -10.4f);
        using (var p = Stroke(c))
        {
            p.Width = 1.8f;
            g.DrawLine(p, 11f, 4.2f, 11f, 13.4f);
            g.DrawLine(p, 7.2f, 9.7f, 11f, 13.6f);
            g.DrawLine(p, 14.8f, 9.7f, 11f, 13.6f);
            using (var tray = new GraphicsPath())
            {
                tray.AddLine(4.8f, 13.6f, 4.8f, 16.4f);
                tray.AddLine(4.8f, 16.4f, 17.2f, 16.4f);
                tray.AddLine(17.2f, 16.4f, 17.2f, 13.6f);
                g.DrawPath(p, tray);
            }
        }
        g.Restore(st);
    });

    public static readonly Color CloseColor = Color.FromArgb(230, 72, 77);
    public static readonly Color CheckColor = Color.FromArgb(42, 168, 86);

    public static readonly Action<Graphics, Rectangle, Color> Close = Icon((g, c) =>
    {
        var st = g.Save();
        g.TranslateTransform(11f, 11f);
        g.ScaleTransform(1.12f, 1.12f);
        g.TranslateTransform(-11f, -11f);
        using (var p = Stroke(CloseColor))
        {
            p.Width = 1.9f;
            g.DrawLine(p, 6.2f, 6.2f, 15.8f, 15.8f);
            g.DrawLine(p, 15.8f, 6.2f, 6.2f, 15.8f);
        }
        g.Restore(st);
    });

    public static readonly Action<Graphics, Rectangle, Color> Check = Icon((g, c) =>
    {
        var st = g.Save();
        g.TranslateTransform(11f, 11.2f);
        g.ScaleTransform(1.18f, 1.18f);
        g.TranslateTransform(-11f, -11.2f);
        using (var p = Stroke(CheckColor))
        {
            p.Width = 2.0f;
            g.DrawLine(p, 4.6f, 11.6f, 9.2f, 16.2f);
            g.DrawLine(p, 9.2f, 16.2f, 17.4f, 6.2f);
        }
        g.Restore(st);
    });

    // Undo is dimmed while there is nothing to undo.
    public static bool UndoEnabled = true;

    public static readonly Action<Graphics, Rectangle, Color> Undo = Icon((g, c) =>
    {
        var ink = UndoEnabled ? c : (Program.DarkMode ? Color.FromArgb(96, 96, 96) : Color.FromArgb(198, 198, 198));
        // Match the download glyph's footprint: recentre and scale the loop up.
        var state = g.Save();
        g.TranslateTransform(11f, 10.6f);
        g.ScaleTransform(1.16f, 1.16f);
        g.TranslateTransform(-12.8f, -8.9f);
        using (var pen = new Pen(ink, 1.7f))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            // Upright loop like the reference: flat top bar, half turn on the right,
            // flat bottom bar back to the left, arrowhead on the top bar's end.
            using (var path = new GraphicsPath())
            {
                path.AddLine(7.4f, 6.0f, 14.0f, 6.0f);
                path.AddArc(9.8f, 6.0f, 8.4f, 8.4f, 270f, 180f);
                path.AddLine(14.0f, 14.4f, 5.6f, 14.4f);
                g.DrawPath(pen, path);
            }
            using (var head = new GraphicsPath())
            {
                head.AddLine(10.8f, 3.4f, 7.4f, 6.0f);
                head.AddLine(7.4f, 6.0f, 10.8f, 8.6f);
                g.DrawPath(pen, head);
            }
        }
        g.Restore(state);
    });
}
public class LoupeForm : Form
{
    // If a click ever gives it focus anyway, keep the overlay's shortcuts working.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var owner = Owner as SnipForm;
        if (owner != null && owner.HandleShortcut(keyData & Keys.KeyCode, (keyData & Keys.Control) == Keys.Control))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static readonly Font InfoFont = new Font("Microsoft YaHei UI", 9F);
    Bitmap screen;
    Bitmap wallpaper;
    Point origin;
    Point pt;
    bool ready;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int ini);

    public LoupeForm(Bitmap bmp, Point o)
    {
        screen = bmp;
        origin = o;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // The English hint wraps onto two lines, so the card needs a bit more height.
        Size = new Size(220, Program.LangZh ? 306 : 330);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        ApplyRegion();
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        try
        {
            var sb = new StringBuilder(512);
            if (SystemParametersInfo(0x73, 512, sb, 0))
            {
                var wp = sb.ToString();
                if (System.IO.File.Exists(wp)) wallpaper = new Bitmap(wp);
            }
        }
        catch (Exception) { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();   // a stale region leaves a strip of background beside the card
    }

    void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        // Round the window itself, otherwise the square white fill always leaves
        // two square corners at the bottom of the card.
        using (var path = Rounded(new Rectangle(0, 0, Width, Height), 12))
        {
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRegion();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;
            cp.ExStyle |= 0x00000080;
            cp.ExStyle |= 0x00000020;
            return cp;
        }
    }

    // The card is TopMost, so once the pointer is over it the overlay stops receiving mouse
    // messages. Refresh from the real cursor on our own timer so it can never freeze.
    readonly System.Windows.Forms.Timer follow = new System.Windows.Forms.Timer { Interval = 33 };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        follow.Tick += (s2, e2) =>
        {
            if (IsDisposed || Disposing) { follow.Stop(); return; }
            try { SetPoint(Cursor.Position); } catch (Exception) { }
        };
        follow.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { follow.Stop(); follow.Dispose(); } catch (Exception) { }
        base.OnFormClosed(e);
    }

    public void SetPoint(Point p)
    {
        // Repaint whenever the point changes, even if the card itself cannot move any
        // further (clamped at a screen edge): returning on an unchanged bounds froze the
        // magnifier at whatever was drawn last while the pointer slid along the border.
        if (ready && p == pt) return;
        pt = p;
        ready = true;
        var vs = SystemInformation.VirtualScreen;
        // 20px above would cover the size label when the cursor is the selection's
        // top-left corner; 46px clears the label (about one line tall).
        int px = p.X + 20, py = p.Y + 46;
        if (px + Width > vs.Right) px = p.X - Width - 20;
        if (py + Height > vs.Bottom) py = p.Y - Height - 20;
        if (px < vs.Left) px = vs.Left;
        if (py < vs.Top) py = vs.Top;
        var target = new Rectangle(px, py, Width, Height);
        if (Bounds != target) Bounds = target;
        Invalidate();
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!ready || screen == null) return;
        var g = e.Graphics;
        var panel = new Rectangle(0, 0, Width, Height);
        bool dark = Program.DarkMode;
        Color cardBack = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
        Color cardBorder = dark ? Color.FromArgb(74, 74, 74) : Color.FromArgb(225, 225, 225);
        Color textColor = dark ? Color.FromArgb(226, 226, 226) : Color.FromArgb(80, 80, 80);
        Color hintColor = Color.FromArgb(150, 150, 150);
        using (var path = Rounded(panel, 10))
        {

            using (var b = new SolidBrush(cardBack)) g.FillRectangle(b, panel);

            // Border first, so the magnified pixels cover it edge to edge. It has to follow
            // the theme: a hard white pen showed up as a bright line beside the text block
            // in dark mode.
            using (var border = new Pen(dark ? Color.FromArgb(86, 86, 86) : Color.White, 1))
                g.DrawPath(border, path);

            int zoom = 5, cols = 44, rows = 44;
            int bx = pt.X - origin.X, by = pt.Y - origin.Y;
            int pw = cols * zoom, ph = rows * zoom;
            var zoomRect = new Rectangle(0, 0, pw, ph);
            int sx = bx - cols / 2;
            int sy = by - rows / 2;
            using (var black = new SolidBrush(Color.Black)) g.FillRectangle(black, zoomRect);
            int ix = Math.Max(0, sx), iy = Math.Max(0, sy);
            int ix2 = Math.Min(screen.Width, sx + cols), iy2 = Math.Min(screen.Height, sy + rows);
            if (ix2 > ix && iy2 > iy)
            {
                var dest = new Rectangle((ix - sx) * zoom, (iy - sy) * zoom, (ix2 - ix) * zoom, (iy2 - iy) * zoom);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                // Half: with an exact 5x nearest-neighbour blit this paints every one
                // of the 220 rows; None leaves the last two rows showing the backing.
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(screen, dest, new Rectangle(ix, iy, ix2 - ix, iy2 - iy), GraphicsUnit.Pixel);
                g.InterpolationMode = InterpolationMode.Default;
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }

            // The crosshair belongs in the *middle* of the pointer's 5px cell: drawn on the
            // cell's edge it looked half a cell off, most visibly in the screen corners.
            int mx = zoomRect.Left + (bx - sx) * zoom + zoom / 2;
            int my = zoomRect.Top + (by - sy) * zoom + zoom / 2;
            mx = Math.Min(zoomRect.Right - 1, Math.Max(zoomRect.Left, mx));
            my = Math.Min(zoomRect.Bottom - 1, Math.Max(zoomRect.Top, my));
            using (var pen = new Pen(Program.Accent, 3))
            {
                g.DrawLine(pen, zoomRect.Left, my, zoomRect.Right, my);
                g.DrawLine(pen, mx, zoomRect.Top, mx, zoomRect.Bottom);
            }

            Color c = Color.Black;
            try { c = screen.GetPixel(Math.Min(screen.Width - 1, Math.Max(0, bx)), Math.Min(screen.Height - 1, Math.Max(0, by))); } catch (Exception) { }
            using (var tb = new SolidBrush(textColor))
            using (var hintBrush = new SolidBrush(hintColor))
            {
                int ty = zoomRect.Bottom + 8;
                g.DrawString(Program.T("坐标: (", "LOC: (") + bx + ", " + by + ")", InfoFont, tb, 14, ty);
                                g.DrawString(Program.T("色值: #", "HEX: #") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"), InfoFont, tb, 14, ty + 22);
                                if (Program.LangZh)
                    g.DrawString(Program.T("按Ctrl+C复制色号", "Press Ctrl+C to copy the colour"), InfoFont, hintBrush, 14, ty + 44);
                else
                {
                    g.DrawString("Press Ctrl+C to", InfoFont, hintBrush, 14, ty + 44);
                    g.DrawString("copy color value", InfoFont, hintBrush, 14, ty + 66);
                }
            }

            // Card outline, drawn over the pixels like WeChat's: on white content the
            // card would otherwise melt into the window behind it. Translucent black
            // stays an edge on dark content instead of turning into a pale line.
            using (var edge = new Pen(Color.FromArgb(dark ? 110 : 46, 0, 0, 0), 2))
                g.DrawPath(edge, path);
        }
    }
}




































