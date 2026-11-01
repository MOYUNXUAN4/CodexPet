using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CodexPet;

public sealed class MainForm : Form
{
    private const int BaseWidth = 292;
    private const int CornerRadius = 14;
    private static readonly Color CardColor = Color.FromArgb(0x10, 0x13, 0x1a);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DataService _data;
    private readonly PetConfig _cfg;
    private readonly MediaWatcher _media = MediaWatcher.Shared;
    private string _linkKey = "";   // 上一次预热过的 歌手/专辑，换歌时才重新查
    private readonly string _captureBase;
    private readonly string _dumpPath;
    private WebView2 _webView;
    private int _webRestarts;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _saveTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _ready;
    private bool _applying;
    private bool _hotkeyOk;
    private string _hotkeyText = "Ctrl+Alt+P";
    private CoreWebView2 _core;
    // 右键菜单只在下一次弹出/关闭窗口时释放：如果在 Closed 里立刻 Dispose，
    // ToolStripManager 还握着引用，下次激活切换会 SetVisibleCore 到已释放对象上，整个进程崩掉
    private ContextMenuStrip _ctxMenu;

    /* ---------------- 全局热键 ---------------- */
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggle = 0x0B01;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkP = 0x50;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static readonly Color MenuBack = Color.FromArgb(0x16, 0x1B, 0x24);
    private static readonly Color MenuFore = Color.FromArgb(0xDD, 0xE6, 0xF2);

    public MainForm(DataService data, PetConfig cfg, string captureBase, string dumpPath = null)
    {
        _data = data;
        _cfg = cfg;
        _captureBase = captureBase;
        _dumpPath = dumpPath;

        Text = "Codex";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = CardColor;
        DoubleBuffered = true;
        MinimizeBox = false;
        MaximizeBox = false;

        _webView = NewWebView();
        HandleDestroyed += (_, _) => Log.Write("form HandleDestroyed");
        HandleCreated += (_, _) => Log.Write("form HandleCreated");
        Controls.Add(_webView);

        _saveTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); if (!Ephemeral) ConfigStore.Save(_cfg); };

        _tray = BuildTray();
        _data.Changed += OnDataChanged;
        Load += OnLoad;
        DpiChanged += (_, _) => BeginInvoke(new Action(FixWidthForDpi));
        FormClosed += OnClosed;
    }

    private double UiScale => DeviceDpi / 96.0;

    // 显示器 DPI 变了（换屏 / 拖到另一块屏）就按新比例重设宽度，页面才不会被裁
    private void FixWidthForDpi()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            var want = (int)Math.Round(BaseWidth * UiScale);
            if (want > 0 && Math.Abs(Width - want) > 1)
            {
                Log.Write($"dpi width fix {Width} -> {want} dpi={DeviceDpi}");
                SetBounds(Left, Top, want, Height);
                ApplyRoundedRegion();
            }
        }
        catch (Exception ex) { Log.Write("dpi fix: " + ex.Message); }
    }

    // 截图 / 导出实例只借用界面，绝不能把布局写回用户配置
    private bool Ephemeral => _captureBase != null || _dumpPath != null;

    private void OnLoad(object sender, EventArgs e)
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        var scale = UiScale;
        var w = (int)Math.Round(BaseWidth * scale);
        var h = _cfg.Height is > 0 ? _cfg.Height.Value : (int)Math.Round(196 * scale);
        var x = _cfg.X ?? wa.Right - w - 32;
        var y = _cfg.Y ?? wa.Bottom - h - 32;

        // 记住了屏幕外的位置（换过显示器/分辨率）就回到主屏右下角
        var vs = SystemInformation.VirtualScreen;
        var visibleX = Math.Min(x + w, vs.Right) - Math.Max(x, vs.Left);
        var visibleY = Math.Min(y + h, vs.Bottom) - Math.Max(y, vs.Top);
        if (visibleX < 80 || visibleY < 60)
        {
            Log.Write($"position off-screen ({x},{y}) virtual={vs}, fallback to default");
            x = wa.Right - w - 32;
            y = wa.Bottom - h - 32;
        }
        // 上次退出时顶部可能被顶到屏幕外（比如内容比屏还高的瞬间），回来时先拉进来
        if (y < wa.Top)
        {
            Log.Write($"clamp top {y} -> {wa.Top}");
            y = wa.Top;
            _cfg.Y = y;
        }
        SetBounds(x, y, w, h);
        ApplyRoundedRegion();
        // 目标显示器不是 100% 时，SetBounds 之后 DeviceDpi 才切过去；宽度必须按新 DPI 重算，
        // 否则网页按 292 DIP 排版、视口只有 240 DIP，右边会被整块裁掉（本月金额就是这么被切掉的）
        BeginInvoke(new Action(FixWidthForDpi));
        try { RegisterHotkeys(); }
        catch (Exception ex) { Log.Write("hotkey: " + ex.Message); }
        Log.Write($"load bounds={x},{y} {w}x{h} dpi={DeviceDpi}");
        if (Ephemeral)
        {
            // 复用用户配置会让截图结果受上次布局影响（比如上次折叠着退出），这里统一归位
            _data.State.Collapsed = false;
            _data.State.Expanded = false;
            _data.State.CurveOpen = false;
            _data.State.MonthOpen = false;
            _data.State.HeatOpen = false;
            _data.State.PetClosed = false;
            _data.State.Mode = "codex";
            Log.Write("ephemeral run: view state reset");
        }
        InitWebViewAsync();
        _ = _data.StartAsync(_cts.Token);

        // 播放器：订阅系统媒体会话，界面收起时也照常轮询（展开就是最新状态）
        _media.Changed += OnMediaChanged;
        _media.Start();
    }

    private void ApplyRoundedRegion()
    {
        try
        {
            if (Width <= 0 || Height <= 0) return;
            var r = (int)Math.Round(CornerRadius * UiScale);
            using var path = new GraphicsPath();
            var d = r * 2;
            var b = new Rectangle(0, 0, Width, Height);
            path.AddArc(b.Left, b.Top, d, d, 180, 90);
            path.AddArc(b.Right - d - 1, b.Top, d, d, 270, 90);
            path.AddArc(b.Right - d - 1, b.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(b.Left, b.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }
        catch (Exception ex) { Log.Write("region: " + ex.Message); }
    }

    private static int _clickLogs;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // 子窗口（WebView2）的鼠标按下会以 WM_PARENTNOTIFY 通知父窗口，用来确认点击是否真的到达面板
        if (m.Msg == 0x0210 && (m.WParam.ToInt64() & 0xFFFF) == 0x0201 && _clickLogs < 40)
        {
            _clickLogs++;
            Log.Write("click-hit #" + _clickLogs);
        }
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyToggle)
        {
            Log.Write("hotkey fired");
            ToggleCollapse();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    private WebView2 NewWebView()
    {
        var wv = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
            AllowExternalDrop = false
        };
        wv.Disposed += (_, _) => Log.Write("webview disposed, stack=" + Environment.StackTrace);
        wv.HandleDestroyed += (_, _) => Log.Write("webview HandleDestroyed, stack=" + Environment.StackTrace);
        wv.HandleCreated += (_, _) => Log.Write("webview HandleCreated");
        return wv;
    }

    // 浏览器进程意外退出时自动重建渲染宿主，避免面板留一块白板
    private void RestartWebView(string reason)
    {
        if (IsDisposed || Disposing) return;
        if (_webRestarts >= 8) { Log.Write("webview restart limit: " + reason); return; }
        _webRestarts++;
        Log.Write($"webview restart #{_webRestarts}: {reason}");

        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            try
            {
                _ready = false;
                _core = null;
                var old = _webView;
                try { Controls.Remove(old); } catch { }
                try { old.Dispose(); } catch { }
                _webView = NewWebView();
                Controls.Add(_webView);
                _webView.BringToFront();
                InitWebViewAsync();
            }
            catch (Exception ex) { Log.Write("restart: " + ex.Message); }
        };
        t.Start();
    }

    private async void InitWebViewAsync()
    {
        try
        {
            // 调试/导出模式可能与正常实例并存，必须用独立的数据目录，否则会卡在浏览器进程初始化
            var debug = _dumpPath != null || _captureBase != null;
            // 连续失败时换一个干净的 profile，绕开被上次异常退出锁住的用户数据目录
            var profile = debug ? "WebView2-debug" : (_webRestarts > 0 ? "WebView2-retry" + _webRestarts : "WebView2");
            var udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPet", profile);
            Directory.CreateDirectory(udf);
            var options = new CoreWebView2EnvironmentOptions
            {
                // 本机 GPU 加速会令浏览器进程崩溃（与 Electron 同样的问题）
                AdditionalBrowserArguments = "--disable-gpu --no-sandbox"
            };
            var env = await CoreWebView2Environment.CreateAsync(null, udf, options);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            _core = core;
            _webRestarts = 0;
            Log.Write("webview ready, core=" + (core != null));
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, ev) =>
            {
                Log.Write("process failed: kind=" + ev.ProcessFailedKind + " exit=" + ev.ExitCode + " desc=" + ev.ProcessDescription);
                if (ev.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    try { BeginInvoke(new Action(() => RestartWebView("browser exited"))); } catch { }
                }
            };
            core.NavigationCompleted += (_, ev) =>
            {
                Log.Write($"nav completed ok={ev.IsSuccess} err={ev.WebErrorStatus} webDisposed={_webView.IsDisposed}");
                _ready = true;
                PushState();
                PushMedia();
                if (_captureBase != null) RunCaptures();
                if (_dumpPath != null) RunDump();
            };
            core.NewWindowRequested += (_, ev) =>
            {
                ev.Handled = true;
                OpenUrl(ev.Uri);
            };

            var indexPath = Path.Combine(AppContext.BaseDirectory, "renderer", "index.html");
            core.Navigate(new Uri(indexPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            Log.Write("webview init: " + ex.Message);
            MessageBox.Show("WebView2 初始化失败：" + ex.Message, "Codex 仪表盘");
        }
    }

    private JsonElement StateElement()
    {
        lock (_data.Sync) { return JsonSerializer.SerializeToElement(_data.State, JsonOpts); }
    }

    private void OnDataChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(new Action(PushState)); } catch { }
    }

    public void PushState()
    {
        if (!_ready || _core == null) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { type = "state", state = StateElement() });
            _webView.CoreWebView2.PostWebMessageAsJson(payload);
            UpdateTray();
        }
        catch { }
    }

    /* ---------------- 播放器 ---------------- */

    private void OnMediaChanged(MediaInfo info)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(new Action(PushMedia)); } catch { }
    }

    public void PushMedia()
    {
        if (_core == null) return;
        try
        {
            var cur = _media.Current;
            // 换歌时顺手把歌手页/专辑页查好，点下去不用等网络
            var key = cur.Artist + "\u0001" + cur.Album;
            if (key != _linkKey)
            {
                _linkKey = key;
                SpotifyLink.PreResolve(cur.Artist, cur.Album);
            }
            var payload = JsonSerializer.Serialize(new { type = "media", media = cur }, JsonOpts);
            _core.PostWebMessageAsJson(payload);
        }
        catch (Exception ex) { Log.Write("media push: " + ex.Message); }
    }

    private void Reply(int id, JsonElement result)
    {
        if (id == 0 || _core == null) return;
        var payload = JsonSerializer.Serialize(new { type = "res", id, ok = true, result });
        try { _core.PostWebMessageAsJson(payload); } catch { }
    }

    private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var ide) && ide.ValueKind == JsonValueKind.Number ? ide.GetInt32() : 0;
            var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : "";
            var args = root.TryGetProperty("args", out var a) ? a : default;

            switch (cmd)
            {
                case "get-state":
                    Reply(id, StateElement());
                    break;

                case "refresh":
                    await _data.RefreshAllAsync();
                    Reply(id, StateElement());
                    break;

                case "get-position":
                    Reply(id, JsonSerializer.SerializeToElement(new { x = Left, y = Top }));
                    break;

                case "drag":
                    DoDrag(args);
                    break;

                case "resize":
                    if (args.ValueKind == JsonValueKind.Number) ApplyCssHeight(args.GetDouble());
                    break;

                case "set-view":
                    await SetViewAsync(args);
                    Reply(id, StateElement());
                    break;

                case "probe":
                    if (_clickLogs < 40) { _clickLogs++; Log.Write("click-js " + (args.ValueKind == JsonValueKind.String ? args.GetString() : "")); }
                    break;

                case "quit":
                    Close();
                    break;

                case "open-external":
                    if (args.ValueKind == JsonValueKind.String) OpenUrl(args.GetString());
                    break;

                case "open-session":
                    OpenSession(args.ValueKind == JsonValueKind.String ? args.GetString() : null);
                    break;

                case "menu":
                    ShowContextMenu(args);
                    break;

                case "get-media":
                    Reply(id, JsonSerializer.SerializeToElement(_media.Current, JsonOpts));
                    break;

                case "media-toggle":
                    MediaCommand("toggle", args);
                    break;

                case "media-next":
                    MediaCommand("next", args);
                    break;

                case "media-prev":
                    MediaCommand("prev", args);
                    break;

                case "media-seek":
                    MediaCommand("seek", args);
                    break;

                case "media-like":
                {
                    // 要抢一下 Spotify 的前台才能按键，别堵 UI 线程
                    var rl = id;
                    _ = Task.Run(() =>
                    {
                        var ok = SpotifyLink.SendLike();
                        if (rl == 0) return;
                        try { BeginInvoke(new Action(() => Reply(rl, JsonSerializer.SerializeToElement(new { ok })))); } catch { }
                    });
                    break;
                }

                case "media-open":
                    // 反查 + 拉起 Spotify 都别堵 UI 线程
                    SpotifyLink.Open(args.ValueKind == JsonValueKind.String ? args.GetString() : "track", _media.Current);
                    break;
            }
        }
        catch (Exception ex) { Log.Write("msg: " + ex.Message); }
    }

    // GSMTC 的调用会阻塞几百毫秒（Spotify 那边还可能更久），必须离开 UI 线程
    private void MediaCommand(string cmd, JsonElement args)
    {
        _ = Task.Run(() =>
        {
            switch (cmd)
            {
                case "toggle": _media.TogglePlayPause(); break;
                case "next": _media.Next(); break;
                case "prev": _media.Prev(); break;
                case "seek":
                    if (args.ValueKind == JsonValueKind.Number) _media.Seek(args.GetDouble());
                    break;
            }
        });
    }

    private void DoDrag(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 4) return;
        var dx = args[0].GetDouble();
        var dy = args[1].GetDouble();
        var sx = args[2].GetDouble();
        var sy = args[3].GetDouble();
        var s = UiScale;
        Location = new Point((int)Math.Round(sx + dx * s), (int)Math.Round(sy + dy * s));
        _cfg.X = Left;
        _cfg.Y = Top;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ApplyCssHeight(double cssHeight)
    {

        if (_applying) return;
        var target = (int)Math.Round(Math.Clamp(cssHeight, 24, 900) * UiScale);
        if (target <= 0 || Math.Abs(Height - target) < 1) return;

        _applying = true;
        try
        {
            var wa = Screen.FromControl(this).WorkingArea;
            var newTop = Top + Height - target;
            // 内容比工作区还高（小屏 / 面板全展开）时，别把顶部顶出屏幕：
            // 播放器在最上面，看不见就等于没有
            var lowest = Math.Max(wa.Top, wa.Bottom - target);
            newTop = Math.Min(Math.Max(newTop, wa.Top), lowest);
            SetBounds(Left, newTop, Width, target);
            _cfg.Height = target;
            _cfg.Y = newTop;
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        finally { _applying = false; }
    }

    private async Task SetViewAsync(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return;

        if (args.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
            _data.State.Mode = m.GetString() ?? "codex";
        if (args.TryGetProperty("expanded", out var ex) && ex.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _data.State.Expanded = ex.GetBoolean();
        if (args.TryGetProperty("curveOpen", out var cv) && cv.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _data.State.CurveOpen = cv.GetBoolean();
        if (args.TryGetProperty("monthOpen", out var mo) && mo.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _data.State.MonthOpen = mo.GetBoolean();
        if (args.TryGetProperty("heatOpen", out var ho) && ho.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _data.State.HeatOpen = ho.GetBoolean();
        if (args.TryGetProperty("petClosed", out var pc) && pc.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _data.State.PetClosed = pc.GetBoolean();
        if (args.TryGetProperty("collapsed", out var co) && co.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _data.State.Collapsed = co.GetBoolean();

        _cfg.Mode = _data.State.Mode;
        _cfg.Expanded = _data.State.Expanded;
        _cfg.CurveOpen = _data.State.CurveOpen;
        _cfg.MonthOpen = _data.State.MonthOpen;
        _cfg.HeatOpen = _data.State.HeatOpen;
        _cfg.PetClosed = _data.State.PetClosed;
        _cfg.Collapsed = _data.State.Collapsed;
        if (!Ephemeral) ConfigStore.Save(_cfg);

        if (_data.State.Mode == "antigravity" && _data.State.Antigravity == null) await _data.FetchAgAsync();
        if (_data.State.Mode == "workbuddy" && _data.State.Wb == null) _data.CollectWb();
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /* ---------------- 右键菜单 / 热键 ---------------- */

    // Ctrl+Alt+P 可能被别的软件占了，按顺序往后试几个等价组合
    private void RegisterHotkeys()
    {
        var candidates = new (uint Mod, uint Vk, string Text)[]
        {
            (ModControl | ModAlt | ModNoRepeat, VkP, "Ctrl+Alt+P"),
            (ModControl | ModAlt | ModNoRepeat, 0x4F, "Ctrl+Alt+O"),
            (ModControl | ModShift | ModNoRepeat, VkP, "Ctrl+Shift+P"),
            (ModAlt | ModShift | ModNoRepeat, VkP, "Alt+Shift+P"),
        };
        foreach (var c in candidates)
        {
            var ok = RegisterHotKey(Handle, HotkeyToggle, c.Mod, c.Vk);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            Log.Write($"hotkey {c.Text} = {ok} err={err}");
            if (ok)
            {
                _hotkeyOk = true;
                _hotkeyText = c.Text;
                return;
            }
        }
        Log.Write("hotkey: all combos taken, 全局热键不可用");
    }

    private void ToggleCollapse()
    {
        try
        {
            if (!Visible) { Show(); return; }
            _core?.ExecuteScriptAsync("if(typeof applyCollapsed==='function')applyCollapsed(!collapsed,true)");
        }
        catch (Exception ex) { Log.Write("toggle: " + ex.Message); }
    }

    private static string ModeName(string mode) => mode switch
    {
        "antigravity" => "ANTIGRAVITY",
        "workbuddy" => "WORKBUDDY",
        _ => "CODEX"
    };

    private void ShowContextMenu(JsonElement args)
    {
        try
        {
            var st = _data.State;
            var mode = st.Mode ?? "codex";

            // 上一张菜单此刻早已关闭，ModalMenuFilter 也不再引用它，这时才安全释放
            try { _ctxMenu?.Dispose(); } catch { }
            _ctxMenu = null;

            var menu = new ContextMenuStrip
            {
                BackColor = MenuBack,
                ForeColor = MenuFore,
                ShowImageMargin = false,
                Font = new Font("Microsoft YaHei UI", 9f),
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors())
            };

            var headText = ModeName(mode);
            var model = mode == "codex" ? st.Session?.Model : mode == "workbuddy" ? st.Wb?.Model : null;
            if (!string.IsNullOrEmpty(model)) headText += "  ·  " + model.Replace('-', ' ').Replace('_', ' ');
            menu.Items.Add(new ToolStripMenuItem(headText)
            {
                Enabled = false,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            });
            menu.Items.Add(new ToolStripSeparator());

            string dir, file = null;
            if (mode == "codex") { dir = st.Session?.Cwd; file = st.Session?.FilePath; }
            else if (mode == "workbuddy") dir = Path.Combine(AppPaths.Home, ".workbuddy", "projects");
            else dir = Path.Combine(AppPaths.Home, ".gemini", "antigravity");

            var openDir = new ToolStripMenuItem("打开会话目录") { Enabled = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) };
            openDir.Click += (_, _) => OpenFolder(dir);
            menu.Items.Add(openDir);

            var openFile = new ToolStripMenuItem("打开当前会话文件")
            {
                ShortcutKeyDisplayString = "双击完成条"
            };
            openFile.Click += (_, _) => OpenSession(mode);
            menu.Items.Add(openFile);

            var copyPath = new ToolStripMenuItem("复制会话路径") { Enabled = !string.IsNullOrEmpty(file) };
            copyPath.Click += (_, _) => CopyText(file);
            menu.Items.Add(copyPath);

            menu.Items.Add(new ToolStripSeparator());

            var refresh = new ToolStripMenuItem("刷新数据");
            refresh.Click += async (_, _) => await _data.RefreshAllAsync();
            menu.Items.Add(refresh);
            menu.Items.Add(new ToolStripMenuItem("折叠 / 展开", null, (_, _) => ToggleCollapse())
            {
                ShortcutKeyDisplayString = _hotkeyText
            });

            var autoStart = new ToolStripMenuItem("开机自动启动")
            {
                Checked = Shortcuts.AutoStartEnabled(),
                CheckOnClick = true
            };
            autoStart.Click += (_, _) =>
            {
                var on = Shortcuts.SetAutoStart(autoStart.Checked);
                autoStart.Checked = on;
                Log.Write("menu autostart=" + on);
            };
            menu.Items.Add(autoStart);
            menu.Items.Add(new ToolStripMenuItem("创建桌面快捷方式", null, (_, _) => Shortcuts.CreateDesktop()));

            menu.Items.Add(new ToolStripMenuItem("隐藏面板", null, (_, _) => Hide()));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("重启面板", null, (_, _) => RestartWebView("menu")));
            menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Close()));

            // 这里绝对不能在 Closed 里同步 Dispose（见 _ctxMenu 字段注释），只做引用交接
            _ctxMenu = menu;

            int x = 0, y = 0;
            if (args.ValueKind == JsonValueKind.Object)
            {
                if (args.TryGetProperty("x", out var px) && px.ValueKind == JsonValueKind.Number) x = (int)px.GetDouble();
                if (args.TryGetProperty("y", out var py) && py.ValueKind == JsonValueKind.Number) y = (int)py.GetDouble();
            }
            menu.Show(x > 0 && y > 0 ? new Point(x, y) : Cursor.Position);
        }
        catch (Exception ex) { Log.Write("menu: " + ex.Message); }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("open folder: " + ex.Message); }
    }

    // 点完成条：直接定位到那个 agent 当前会话的 rollout / transcript 文件
    private void OpenSession(string agent)
    {
        try
        {
            var st = _data.State;
            var who = string.IsNullOrEmpty(agent) ? (st.Mode ?? "codex") : agent;
            string file = who switch
            {
                "workbuddy" => st.WbFile ?? st.Wb?.FilePath,
                "antigravity" => st.AgFile,
                _ => st.Session?.FilePath
            };

            if (!string.IsNullOrEmpty(file) && File.Exists(file))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
                Log.Write("open-session " + who + " -> " + file);
                return;
            }

            // 找不到具体文件就退化成打开目录
            var dir = who switch
            {
                "workbuddy" => Path.Combine(AppPaths.Home, ".workbuddy", "projects"),
                "antigravity" => Path.Combine(AppPaths.Home, ".gemini", "antigravity", "brain"),
                _ => st.Session?.Cwd
            };
            Log.Write("open-session fallback " + who + " dir=" + (dir ?? "null"));
            OpenFolder(dir);
        }
        catch (Exception ex) { Log.Write("open-session: " + ex.Message); }
    }

    private static void CopyText(string text)
    {
        try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); }
        catch (Exception ex) { Log.Write("clipboard: " + ex.Message); }
    }

    /* ---------------- 托盘 ---------------- */

    private NotifyIcon BuildTray()
    {
        var icon = new NotifyIcon { Visible = true, Text = "Codex 仪表盘" };
        try
        {
            var png = Path.Combine(AppContext.BaseDirectory, "assets", "tray.png");
            if (File.Exists(png))
            {
                using var bmp = new Bitmap(png);
                icon.Icon = Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch { }

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleVisible());
        menu.Items.Add("立即刷新", null, async (_, _) => await _data.RefreshAllAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("额度页面", null, (_, _) => OpenUrl("https://chatgpt.com/codex/settings/usage"));
        menu.Items.Add("重置追踪", null, (_, _) => OpenUrl("https://codex-reset.com/zh"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("创建桌面快捷方式", null, (_, _) =>
        {
            var ok = Shortcuts.CreateDesktop();
            icon.ShowBalloonTip(3000, "Codex 仪表盘",
                ok ? "桌面快捷方式已创建" : "创建失败，请检查桌面目录权限", ToolTipIcon.Info);
        });
        var auto = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = Shortcuts.AutoStartEnabled() };
        auto.Click += (_, _) =>
        {
            var ok = Shortcuts.SetAutoStart(auto.Checked);
            auto.Checked = ok;
        };
        menu.Items.Add(auto);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());

        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleVisible(); };
        return icon;
    }

    private void UpdateTray()
    {
        try
        {
            var u = _data.State.Usage;
            if (u?.FiveHour != null)
            {
                var a = Math.Round(u.FiveHour.Remaining);
                var b = u.Weekly == null ? "--" : Math.Round(u.Weekly.Remaining).ToString();
                _tray.Text = $"Codex  5h {a}%  ·  周 {b}%";
            }
            else _tray.Text = "Codex 仪表盘";
        }
        catch { }
    }

    private void ToggleVisible()
    {
        if (Visible && WindowState != FormWindowState.Minimized) Hide();
        else Show();
    }

    /* ---------------- 调试截图 ---------------- */

    private async void RunDump()
    {
        try
        {
            await Task.Delay(9000);
            const string script = @"JSON.stringify((() => {
  const t = (id) => { const e = document.getElementById(id); return e ? e.textContent.trim() : null; };
  const d = window.__dbg || {};
  const app = document.getElementById('app');
  const cov = (id) => {
    const cv = document.getElementById(id);
    if (!cv || !cv.width) return null;
    const ctx = cv.getContext('2d');
    const im = ctx.getImageData(0, 0, cv.width, cv.height).data;
    let on = 0;
    for (let i = 3; i < im.length; i += 4) if (im[i] > 8) on++;
    return Math.round((on / (cv.width * cv.height)) * 1000) / 10;
  };
  return {
    csum: t('csum'), c5top: t('c5top'), cwtop: t('cwtop'), mosum: t('mosum'),
    csum2: document.getElementById('csum2') ? document.getElementById('csum2').innerText.trim() : null,
    v5: t('v5'), vw: t('vw'), r5: t('r5'), rw: t('rw'),
    sname: t('sname'), sstate: t('sstate'), fsum: t('fsum'),
    smodel: (window.__state && window.__state.session || {}).model || null,
    seffort: (window.__state && window.__state.session || {}).effort || null,
    ssub: (window.__state && window.__state.session || {}).subagent || null,
    spath: (window.__state && window.__state.session || {}).cwd || null,
    slast: (window.__state && window.__state.session || {}).lastType || null,
    submodel: (window.__state && window.__state.session || {}).subModel || null,
    subeffort: (window.__state && window.__state.session || {}).subEffort || null,
    subnick: (window.__state && window.__state.session || {}).subNick || null,
    subrun: (window.__state && window.__state.session || {}).subRunning || false,
    nameHtml: document.getElementById('sname') ? document.getElementById('sname').innerHTML : null,
    nameW: (function(){ var e = document.getElementById('sname'); return e ? [e.scrollWidth, e.clientWidth] : null; })(),
    title5: document.getElementById('c5')?.parentElement?.title,
    titleW: document.getElementById('cw')?.parentElement?.title,
    usageError: (window.__state && window.__state.usageError) || null,
    usageKeys: window.__state && window.__state.usage ? Object.keys(window.__state.usage) : null,
    appH: app ? Math.round(app.getBoundingClientRect().height) : null,
    agHtml: document.getElementById('agList') ? document.getElementById('agList').innerHTML : null,
    agFoot: t('agFoot'),
    agErr: (window.__state && window.__state.agError) || null,
    agGroups: (window.__state && window.__state.antigravity && window.__state.antigravity.groups) || null,
    wbModel: t('wbModel'), wbModelSub: t('wbModelSub'),
    wbRunning: (window.__state && window.__state.wb && window.__state.wb.running) || false,
    wbModelRaw: (window.__state && window.__state.wb && window.__state.wb.model) || null,
    wbHtml: document.getElementById('viewWb') ? document.getElementById('viewWb').innerText.replace(/\n+/g, ' | ') : null,
    ink5: cov('c5'), inkW: cov('cw'), inkMo: cov('cmo'),
    win5: d.r5, winW: d.rw, winMo: d.rm, pts5: d.h5, ptsW: d.wk, ptsMo: d.mo, now: d.now,
    cmotop: t('cmotop'), csum2: t('csum2'),
    skCodex: document.getElementById('skCodex') ? document.getElementById('skCodex').innerText : null,
    skWb: document.getElementById('skWb') ? document.getElementById('skWb').innerText : null,
    skState: (window.__state && window.__state.session && window.__state.session.skills) || null,
    collapsed: app ? app.classList.contains('collapsed') : null,
    petState: document.getElementById('petStrip') ? document.getElementById('petStrip').dataset.state : null,
    petSay: t('petSay'), petSub: t('petSub'),
    heatCells: document.querySelectorAll('#heat i').length,
    heatReady: (window.__state && window.__state.heat && window.__state.heat.ready) || false,
    heatPeak: t('heatPeak'), heatTotal: t('heatTotal'), heatsum: t('heatsum'),
    moTotal: (window.__state && window.__state.heat && window.__state.heat.monthTotal) || 0,
    moCost: (window.__state && window.__state.heat && window.__state.heat.monthCost) || 0,
    moMix: (window.__state && window.__state.heat && window.__state.heat.monthModels) || [],
    cmoTitle: document.getElementById('cmo') && document.getElementById('cmo').parentElement
      ? document.getElementById('cmo').parentElement.title : null,
    moPts: (window.__state && window.__state.heat && window.__state.heat.month && window.__state.heat.month.length) || 0
  };
})())";
            var json = await _core.ExecuteScriptAsync(script);
            File.WriteAllText(_dumpPath, json ?? "null");
            Log.Write("dump written " + _dumpPath);
        }
        catch (Exception ex) { Log.Write("dump: " + ex); }
        try { Close(); } catch { }
    }

    private async void RunCaptures()
    {
        try
        {
            await Task.Delay(7000);
            await CaptureTo("codex");
            await _core.ExecuteScriptAsync(
                "if(!document.getElementById('curveBody').classList.contains('open'))document.getElementById('foldCurve').click()");
            await Task.Delay(800);
            await _core.ExecuteScriptAsync(
                "if(!document.getElementById('monthBody').classList.contains('open'))document.getElementById('foldMonth').click()");
            await Task.Delay(1600);
            await CaptureTo("curve");
            await _core.ExecuteScriptAsync(
                "if(!document.getElementById('heatBody').classList.contains('open'))document.getElementById('foldHeat').click()");
            await Task.Delay(1000);
            await CaptureTo("heat");
            // 模拟鼠标停在周曲线 62% 位置，检查准星 + 数值气泡
            await _core.ExecuteScriptAsync("(function(){var cv=document.getElementById('cw');var r=cv.getBoundingClientRect();cv.dispatchEvent(new MouseEvent('mousemove',{clientX:r.left+r.width*0.62,clientY:r.top+r.height/2,bubbles:true}));})()");
            await Task.Delay(700);
            await CaptureTo("hover");
            await _core.ExecuteScriptAsync("document.getElementById('cmo').dispatchEvent(new MouseEvent('mouseleave',{bubbles:true}))");
            await _core.ExecuteScriptAsync("document.querySelector('[data-mode=\\\"workbuddy\\\"]').click()");
            await Task.Delay(2200);
            await CaptureTo("wb");
            await _core.ExecuteScriptAsync("document.querySelector('[data-mode=\\\"antigravity\\\"]').click()");
            await Task.Delay(2600);
            await CaptureTo("ag");
            await _core.ExecuteScriptAsync("document.querySelector('[data-mode=\\\"codex\\\"]').click()");
            await Task.Delay(800);
            await CaptureTo("final");
            // 链路自检：真点一下暂停再点回来（日志里应该看到两次 media toggle -> True）
            await _core.ExecuteScriptAsync("document.getElementById('npPlay').click()");
            await Task.Delay(1100);
            await _core.ExecuteScriptAsync("document.getElementById('npPlay').click()");
            await Task.Delay(1100);
            // 播放器：真实播放态
            await CaptureTo("np");
            // 已收藏的样子（只改样式，不动真实曲目）
            await _core.ExecuteScriptAsync(
                "(function(){var n=document.getElementById('np');n.dataset.liked='1';" +
                "var f=document.getElementById('npFav');f.classList.add('pop');" +
                "f.title='已加入「我喜欢」，再点一次取消';})()");
            await Task.Delay(520);
            await CaptureTo("npfav");
            await _core.ExecuteScriptAsync(
                "(function(){document.getElementById('np').dataset.liked='0';" +
                "document.getElementById('npFav').title='收藏到「我喜欢的音乐」';})()");
            await Task.Delay(200);
            // 冻住推送，免得 2 秒一次的状态把摆拍的暂停态冲回去
            await _core.ExecuteScriptAsync("window.__npFreeze=true");
            await _core.ExecuteScriptAsync(
                "(function(){applyMedia({on:true,app:'Spotify',title:media.title,artist:media.artist," +
                "album:media.album,pos:media.pos,dur:media.dur,playing:false,at:Date.now()});})()");
            await Task.Delay(420);
            await CaptureTo("nppause");
            // 没有播放器在放东西时的样子
            await _core.ExecuteScriptAsync("applyMedia({on:false,at:Date.now()})");
            await Task.Delay(340);
            await CaptureTo("npnone");
            // 还原成真实状态
            await _core.ExecuteScriptAsync("window.__npFreeze=false;window.pet.getMedia().then(function(m){if(m)applyMedia(m);})");
            await Task.Delay(700);
            await _core.ExecuteScriptAsync("document.getElementById('foldPet').click()");
            await Task.Delay(700);
            await CaptureTo("npfold");
            await _core.ExecuteScriptAsync("document.getElementById('foldPet').click()");
            await Task.Delay(500);
            // 整卡折叠
            await _core.ExecuteScriptAsync("document.getElementById('collapse').click()");
            await Task.Delay(1400);
            await CaptureTo("collapsed");
            await _core.ExecuteScriptAsync("document.getElementById('collapse').click()");
            await Task.Delay(700);
            // 右键菜单是原生窗口，得抓屏幕
            await _core.ExecuteScriptAsync($"window.pet.menu({{x:{(Left + 40)},y:{(Top + 60)}}})");
            await Task.Delay(800);
            CaptureScreenTo("menu");
            try { SendKeys.Send("{ESC}"); } catch { }
            await Task.Delay(400);
        }
        catch (Exception ex) { Log.Write("capture: " + ex.Message); }
        try { Close(); } catch { }
    }

    private void CaptureScreenTo(string name)
    {
        try
        {
            var r = new Rectangle(Left - 10, Top - 10, Width + 230, Height + 260);
            var vs = SystemInformation.VirtualScreen;
            r.Intersect(vs);
            if (r.Width <= 0 || r.Height <= 0) return;
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save($"{_captureBase}-{name}.png");
            Log.Write("screen capture " + name);
        }
        catch (Exception ex) { Log.Write("screen capture fail: " + ex.Message); }
    }

    private async Task CaptureTo(string name)
    {
        var path = $"{_captureBase}-{name}.png";
        try
        {
            Log.Write($"capture {name}: core={(_core == null ? "null" : "ok")} formBounds={Bounds} webBounds={_webView.Bounds} visible={Visible}");
            if (_core == null) return;
            // 透明背景时 CapturePreview 会报 0x8007139F，截图前临时切回不透明
            _webView.DefaultBackgroundColor = Color.White;
            await using var fs = File.Create(path);
            await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            _webView.DefaultBackgroundColor = Color.Transparent;
            Log.Write("capture " + path + " size=" + new FileInfo(path).Length);
        }
        catch (Exception ex) { Log.Write("capture fail: " + ex); }
    }

    private void OnClosed(object sender, FormClosedEventArgs e)
    {
        Log.Write("closed, reason=" + e.CloseReason + ", stack=" + Environment.StackTrace);
        try { _cts.Cancel(); } catch { }
        try { _media.Changed -= OnMediaChanged; } catch { }
        if (_hotkeyOk) { try { UnregisterHotKey(Handle, HotkeyToggle); } catch { } }
        _saveTimer.Stop();
        _cfg.X = Left;
        _cfg.Y = Top;
        _cfg.Height = Height;
        if (!Ephemeral) ConfigStore.Save(_cfg);
        _tray.Visible = false;
        _tray.Dispose();
        try { _ctxMenu?.Dispose(); } catch { }
        _ctxMenu = null;
    }
}
