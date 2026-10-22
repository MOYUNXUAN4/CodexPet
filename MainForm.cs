using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Diagnostics;
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
    private readonly string _captureBase;
    private readonly string _dumpPath;
    private WebView2 _webView;
    private int _webRestarts;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _saveTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _ready;
    private bool _applying;
    private CoreWebView2 _core;

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
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ConfigStore.Save(_cfg); };

        _tray = BuildTray();
        _data.Changed += OnDataChanged;
        Load += OnLoad;
        FormClosed += OnClosed;
    }

    private double UiScale => DeviceDpi / 96.0;

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
        SetBounds(x, y, w, h);
        ApplyRoundedRegion();
        Log.Write($"load bounds={x},{y} {w}x{h} dpi={DeviceDpi}");
        InitWebViewAsync();
        _ = _data.StartAsync(_cts.Token);
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
            }
        }
        catch (Exception ex) { Log.Write("msg: " + ex.Message); }
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
        var target = (int)Math.Round(Math.Clamp(cssHeight, 120, 760) * UiScale);
        if (target <= 0 || Math.Abs(Height - target) < 1) return;

        _applying = true;
        try
        {
            var wa = Screen.FromControl(this).WorkingArea;
            var newTop = Top + Height - target;
            newTop = Math.Min(Math.Max(newTop, wa.Top), wa.Bottom - target);
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

        _cfg.Mode = _data.State.Mode;
        _cfg.Expanded = _data.State.Expanded;
        _cfg.CurveOpen = _data.State.CurveOpen;
        ConfigStore.Save(_cfg);

        if (_data.State.Mode == "antigravity" && _data.State.Antigravity == null) await _data.FetchAgAsync();
        if (_data.State.Mode == "workbuddy" && _data.State.Wb == null) _data.CollectWb();
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
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
    csum: t('csum'), c5top: t('c5top'), cwtop: t('cwtop'),
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
    ink5: cov('c5'), inkW: cov('cw'),
    win5: d.r5, winW: d.rw, pts5: d.h5, ptsW: d.wk, now: d.now
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
            await _core.ExecuteScriptAsync("document.getElementById('foldCurve').click()");
            await Task.Delay(1800);
            await CaptureTo("curve");
            await _core.ExecuteScriptAsync("document.querySelector('[data-mode=\\\"workbuddy\\\"]').click()");
            await Task.Delay(2200);
            await CaptureTo("wb");
            await _core.ExecuteScriptAsync("document.querySelector('[data-mode=\\\"antigravity\\\"]').click()");
            await Task.Delay(2600);
            await CaptureTo("ag");
            await _core.ExecuteScriptAsync("document.querySelector('[data-mode=\\\"codex\\\"]').click()");
            await Task.Delay(800);
            await CaptureTo("final");
        }
        catch (Exception ex) { Log.Write("capture: " + ex.Message); }
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
        _saveTimer.Stop();
        _cfg.X = Left;
        _cfg.Y = Top;
        _cfg.Height = Height;
        ConfigStore.Save(_cfg);
        _tray.Visible = false;
        _tray.Dispose();
    }
}
