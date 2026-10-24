using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPet;

public sealed class DataService
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/codex/usage";
    private const string ForecastUrl = "https://codex-reset.com/api/forecast";
    private const string AgStatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string AgQuotaPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    // 月曲线要从「本月 5 号」开始算，历史至少留 40 天
    private const long HistKeepMs = 40L * 86400000;
    // 老版本 language_server 监听 12370+，新版本改成 32xx，顺序仅作兜底，实际端口动态探测
    private static readonly int[] AgPorts = Enumerable.Range(12370, 11).ToArray();

    private static readonly Regex CwdRe = new("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex ThreadSourceRe = new("\"thread_source\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex NicknameRe = new("\"agent_nickname\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex AgentPathRe = new("\"agent_path\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex ParentRe = new("\"parent_thread_id\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex SettingsRe = new("\"settings\"\\s*:\\s*\\{\\s*\"model\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"reasoning_effort\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ModelRe = new("\"model\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex EffortRe = new("\"effort\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex CsrfRe = new("\"csrfToken\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ModelSuffixRe = new("\\s*\\((?:Thinking|High|Medium|Low)\\)\\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // skill 调用痕迹：读 SKILL.md 的路径，或 Skill 工具的 {"skill":"名字"} 参数
    private static readonly Regex SkillPathRe = new("skills[\\\\/]+([A-Za-z0-9][A-Za-z0-9._\\-]{1,38})[\\\\/]+SKILL\\.md",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SkillArgRe = new("\"skill\"\\s*:\\s*\"([A-Za-z0-9][A-Za-z0-9._\\-]{1,38})\"", RegexOptions.Compiled);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _agHttp;
    private readonly Dictionary<string, (string Cwd, string ThreadSource, string Nickname, string AgentPath, string Parent)> _metaCache = new();
    private readonly object _histLock = new();

    private HistoryFile _hist = new();
    private DateTime _lastUsage = DateTime.MinValue;
    private DateTime _lastForecast = DateTime.MinValue;
    private DateTime _lastWb = DateTime.MinValue;
    private DateTime _lastAg = DateTime.MinValue;
    private DateTime _lastHistSave = DateTime.MinValue;
    private string _agPort;
    private string _agCsrf;
    // 0=未定（先试代理）1=代理可用 2=直连可用
    private int _netMode;
    // 0=未定 1=内置 HTTP 2=curl 兜底
    private int _usageTransport;
    private string _proxyUrl;
    private HttpClient _proxiedHttp;

    /* ---------------- 完成提示 ---------------- */
    // 最近完成的任务，新的在前；Seq 单调递增供前端判断新旧
    private readonly List<DoneEvent> _doneEvents = new();
    private int _doneSeq;
    // Codex 靠「运行中 → 空闲」的边沿判断
    private bool _cxRunning;
    private long _cxSince;
    private long _cxLastFire;
    // WB / AG 有明确的收尾事件，记住已消费的那一条以免重复上报
    private long _wbDoneKey;
    private long _agDoneKey;
    private long _lastWbPoll;
    private long _lastAgPoll;

    public PetState State { get; } = new();
    public PetConfig Config { get; }
    public readonly object Sync = new();
    public event Action Changed;

    public DataService(PetConfig cfg)
    {
        Config = cfg;
        State.Mode = cfg.Mode is "antigravity" or "workbuddy" ? cfg.Mode : "codex";
        State.Expanded = cfg.Expanded;
        State.CurveOpen = cfg.CurveOpen;
        State.MonthOpen = cfg.MonthOpen;
        State.HeatOpen = cfg.HeatOpen;
        State.Collapsed = cfg.Collapsed;
        // 成本估算：默认按 $2/百万 output token、汇率 7.1，可在配置里覆盖
        State.CostPerMTok = cfg.PricePerMTok is > 0 ? cfg.PricePerMTok.Value : 2.0;
        State.FxRate = cfg.FxRate is > 0 ? cfg.FxRate.Value : 7.1;
        _usageTransport = cfg.NetTransport == "curl" ? 2 : cfg.NetTransport == "http" ? 1 : 0;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _agHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        LoadHistoryFile();
        RefreshHistoryPayload();
        CollectSession();
        await RefreshAllAsync();
        _ = Task.Run(async () =>
        {
            try { await BackfillHistoryAsync(); } catch (Exception ex) { Log.Write("backfill: " + ex.Message); }
        }, ct);
        _ = Task.Run(() =>
        {
            try
            {
                LoadHeatCache();
                BuildHeat();
            }
            catch (Exception ex) { Log.Write("heat start: " + ex.Message); }
        }, ct);
        _ = Task.Run(() => LoopAsync(ct), ct);
    }

    public async Task RefreshAllAsync()
    {
        var tasks = new List<Task> { FetchUsageAsync(), FetchForecastAsync() };
        if (State.Mode == "antigravity" || State.Antigravity == null) tasks.Add(FetchAgAsync());
        await Task.WhenAll(tasks);
        try { CollectWb(); } catch { }
        CollectSession();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                CollectSession();
                if (NowMs() - _lastWbPoll >= 4000) PollWbDone();
                if (NowMs() - _lastAgPoll >= 4000) PollAgDone();
                if ((DateTime.UtcNow - _lastUsage).TotalSeconds >= 60) await FetchUsageAsync();
                if ((DateTime.UtcNow - _lastForecast).TotalMinutes >= 10) await FetchForecastAsync();
                if ((DateTime.UtcNow - _lastWb).TotalMinutes >= 5) CollectWb();
                if ((DateTime.UtcNow - _lastHeatBuild).TotalMinutes >= 10) BuildHeat();
                if ((DateTime.UtcNow - _lastHistSave).TotalMinutes >= 5)
                {
                    RefreshHistoryPayload();
                    SaveHistoryFile();
                }
                if (State.Mode == "antigravity" && (DateTime.UtcNow - _lastAg).TotalSeconds >= 60) await FetchAgAsync();
                Raise();
            }
            catch (Exception ex) { Log.Write("loop: " + ex.Message); }
            try { await Task.Delay(5000, ct); } catch { return; }
        }
    }

    /* ---------------- Codex 登录态 ---------------- */

    private (string Token, string AccountId)? ReadAuth()
    {
        try
        {
            var path = Path.Combine(AppPaths.CodexHome, "auth.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)) return null;
            if (!tokens.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String) return null;
            var token = at.GetString();
            if (string.IsNullOrEmpty(token)) return null;
            var acc = tokens.TryGetProperty("account_id", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            return (token, acc);
        }
        catch { return null; }
    }

    /* ---------------- 额度 ---------------- */

    // 本机访问 chatgpt.com 需要走本地代理，否则 Cloudflare 直接返回 403 HTML 拦截页。
    // 顺序：环境变量 → 系统代理；代理失败再退回直连。
    private string ResolveProxyUrl()
    {
        foreach (var key in new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy" })
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        try
        {
            var sp = WebRequest.GetSystemWebProxy();
            var probe = new Uri(UsageUrl);
            if (sp != null && !sp.IsBypassed(probe))
            {
                var addr = sp.GetProxy(probe);
                if (addr != null && addr.ToString() != UsageUrl) return addr.ToString();
            }
        }
        catch { }
        return null;
    }

    private HttpClient ProxiedClient()
    {
        if (_proxiedHttp != null) return _proxiedHttp;
        var handler = new HttpClientHandler();
        _proxyUrl ??= ResolveProxyUrl();
        if (!string.IsNullOrEmpty(_proxyUrl))
        {
            try { handler.Proxy = new WebProxy(_proxyUrl); handler.UseProxy = true; }
            catch { }
        }
        _proxiedHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return _proxiedHttp;
    }

    private static string ProxyTag(string url)
    {
        if (string.IsNullOrEmpty(url)) return "direct";
        try { var u = new Uri(url); return $"proxy {u.Host}:{u.Port}"; } catch { return "proxy"; }
    }

    private async Task<(HttpResponseMessage Res, string Body)> FetchOnceAsync(
        (string Token, string AccountId) auth, bool useProxy)
    {
        var client = useProxy ? ProxiedClient() : _http;
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.Token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "codex-pet/2.2");
        if (!string.IsNullOrEmpty(auth.AccountId))
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", auth.AccountId);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        return (res, body);
    }

    // Cloudflare 会把 .NET 的请求判成挑战（cf-mitigated: challenge），
    // 系统自带的 curl 走同样的代理却能正常返回，所以作为兜底通道。
    private async Task<(int Code, string Body)> CurlGetAsync(string url, (string Token, string AccountId) auth)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (!File.Exists(exe)) exe = "curl.exe";

        var tag = Guid.NewGuid().ToString("N");
        var cfgPath = Path.Combine(Path.GetTempPath(), "codex-pet-" + tag + ".curl");
        var bodyPath = Path.Combine(Path.GetTempPath(), "codex-pet-" + tag + ".json");
        var sb = new StringBuilder();
        sb.AppendLine("silent");
        sb.AppendLine("location");
        sb.AppendLine("compressed");
        sb.AppendLine("max-time = 25");
        var proxy = _proxyUrl ??= ResolveProxyUrl();
        if (!string.IsNullOrEmpty(proxy))
        {
            try
            {
                var pu = new Uri(proxy);
                sb.AppendLine("proxy = \"" + pu.Scheme + "://" + pu.Host + ":" + pu.Port + "\"");
            }
            catch { }
        }
        sb.AppendLine("url = \"" + url + "\"");
        sb.AppendLine("header = \"Accept: application/json\"");
        sb.AppendLine("header = \"User-Agent: codex-pet/2.2\"");
        if (!string.IsNullOrEmpty(auth.AccountId))
            sb.AppendLine("header = \"ChatGPT-Account-ID: " + auth.AccountId + "\"");
        sb.AppendLine("header = \"Authorization: Bearer " + auth.Token + "\"");

        try
        {
            File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false));
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(cfgPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(bodyPath);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("%{http_code}");

            using var proc = Process.Start(psi);
            if (proc == null) return (0, null);
            var codeText = await proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (!int.TryParse(codeText.Trim(), out var code)) return (0, null);
            var body = File.Exists(bodyPath) ? File.ReadAllText(bodyPath) : null;
            return (code, body);
        }
        catch (Exception ex)
        {
            Log.Write("curl: " + ex.Message);
            return (0, null);
        }
        finally
        {
            try { File.Delete(cfgPath); } catch { }
            try { File.Delete(bodyPath); } catch { }
        }
    }

    private void RememberTransport(int mode)
    {
        _usageTransport = mode;
        var tag = mode == 2 ? "curl" : "http";
        if (Config.NetTransport == tag) return;
        Config.NetTransport = tag;
        ConfigStore.Save(Config);
    }

    private async Task<(int Code, string Body, string How)> GetUsageBodyAsync((string Token, string AccountId) auth)
    {
        if (_usageTransport == 2)
        {
            var c0 = await CurlGetAsync(UsageUrl, auth);
            if (c0.Code == 200) return (200, c0.Body, "curl");
            _usageTransport = 0;
        }

        // 已知可用方式优先；未知时先走代理
        var attempts = _netMode == 2 ? new[] { false, true } : new[] { true, false };
        (int Code, string Body, string How) last = (0, null, "none");

        foreach (var useProxy in attempts)
        {
            if (useProxy && string.IsNullOrEmpty(_proxyUrl ??= ResolveProxyUrl())) continue;
            var how = "http " + ProxyTag(useProxy ? _proxyUrl : null);
            try
            {
                var r = await FetchOnceAsync(auth, useProxy);
                var code = (int)r.Res.StatusCode;
                var challenged = code == 403 && (r.Body ?? "").TrimStart().StartsWith("<");
                r.Res.Dispose();
                last = (code, r.Body, how);
                if (code == 200) { RememberTransport(1); return last; }
                if (!challenged) return last;   // 401/429 之类是真实业务错误，别换通道
                Log.Write("usage challenged " + code + " via " + how);
                _netMode = useProxy ? 2 : 1;    // 记住哪条网络路径是通的
            }
            catch (Exception ex)
            {
                Log.Write("usage " + how + " ex: " + ex.Message);
                last = (0, null, how);
            }
        }

        var c = await CurlGetAsync(UsageUrl, auth);
        if (c.Code == 200) { RememberTransport(2); return (200, c.Body, "curl"); }
        if (c.Code != 0) last = (c.Code, c.Body, "curl");
        return last;
    }

    public async Task FetchUsageAsync()
    {
        var auth = ReadAuth();
        if (auth == null) { State.UsageError = "NO_AUTH"; return; }

        try
        {
            var got = await GetUsageBodyAsync(auth.Value);
            if (got.Code != 200)
            {
                State.UsageError = got.Code switch
                {
                    0 => "NETWORK",
                    401 => "AUTH_EXPIRED",
                    403 => "BLOCKED",
                    _ => "HTTP_" + got.Code
                };
                Log.Write($"usage fail {got.Code} via {got.How} body=" +
                    (got.Body == null ? "" : (got.Body.Length > 160 ? got.Body.Substring(0, 160) : got.Body)));
                return;
            }

            using (var doc = JsonDocument.Parse(got.Body))
            {
                var root = doc.RootElement;
                JsonElement rl = root.TryGetProperty("rate_limit", out var r) ? r : default;

                QuotaWindow Norm(string name)
                {
                    if (rl.ValueKind != JsonValueKind.Object || !rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
                        return null;
                    var used = Num(w, "used_percent") ?? 0;
                    return new QuotaWindow
                    {
                        Remaining = Math.Round(Math.Max(0, Math.Min(100, 100 - used)) * 10) / 10,
                        Used = used,
                        WindowSeconds = (long)(Num(w, "limit_window_seconds") ?? 0),
                        ResetAt = Num(w, "reset_at") is { } ra ? (long)(ra * 1000) : null
                    };
                }

                State.Usage = new UsageInfo
                {
                    Plan = Str(root, "plan_type") ?? "--",
                    FiveHour = Norm("primary_window"),
                    Weekly = Norm("secondary_window"),
                    LimitReached = Bool(rl, "limit_reached")
                };
            }

            State.UsageError = null;
            State.LastUsageAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastUsage = DateTime.UtcNow;
            RecordLiveUsage();
        }
        catch (Exception ex)
        {
            State.UsageError = "NETWORK";
            Log.Write("usage: " + ex.Message);
        }
    }

    /* ---------------- 用量历史 ---------------- */

    private void LoadHistoryFile()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile)) return;
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
            var d = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(AppPaths.HistoryFile), opts);
            if (d != null)
            {
                _hist = d;
                _hist.Samples ??= new List<HistSample>();
                _hist.Scanned ??= new Dictionary<string, ScanStamp>();
            }
        }
        catch (Exception ex) { Log.Write("history load: " + ex.Message); }
    }

    private void SaveHistoryFile()
    {
        try
        {
            lock (_histLock)
            {
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - HistKeepMs;
                _hist.Samples = _hist.Samples.Where(s => s.T >= cutoff).ToList();
                Directory.CreateDirectory(AppPaths.DataDir);
                var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(_hist, opts));
                _lastHistSave = DateTime.UtcNow;
            }
        }
        catch (Exception ex) { Log.Write("history save: " + ex.Message); }
    }

    private static List<HistSample> MergeSamples(IEnumerable<HistSample> list)
    {
        var sorted = list.OrderBy(s => s.T).ToList();
        var outp = new List<HistSample>();
        const long bucket = 2 * 60 * 1000;
        foreach (var s in sorted)
        {
            var last = outp.Count > 0 ? outp[^1] : null;
            if (last != null && s.T - last.T < bucket)
            {
                if (s.P5.HasValue) last.P5 = s.P5;
                if (s.Pw.HasValue) last.Pw = s.Pw;
                last.T = s.T;
            }
            else
            {
                outp.Add(new HistSample { T = s.T, P5 = s.P5, Pw = s.Pw });
            }
        }
        return outp;
    }

    private static List<double[]> Thin(List<double[]> arr, int max)
    {
        if (arr.Count <= max) return arr;
        var step = (double)arr.Count / max;
        var outp = new List<double[]>(max);
        for (var i = 0; i < max; i++)
            outp.Add(arr[Math.Min(arr.Count - 1, (int)Math.Round(i * step))]);
        return outp;
    }

    private void RefreshHistoryPayload()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var h5 = new List<double[]>();
        var wk = new List<double[]>();
        var mo = new List<double[]>();
        var monthFrom = MonthStartMs();
        lock (_histLock)
        {
            foreach (var s in _hist.Samples)
            {
                // 5 小时窗口起点最多可能在 10 小时前（重置时刻在未来），多留一个窗口的历史
                if (s.P5.HasValue && s.T >= now - 13 * 3600L * 1000) h5.Add(new[] { (double)s.T, s.P5.Value });
                if (s.Pw.HasValue && s.T >= now - 8 * 86400L * 1000) wk.Add(new[] { (double)s.T, s.Pw.Value });
                if (s.Pw.HasValue && s.T >= monthFrom) mo.Add(new[] { (double)s.T, s.Pw.Value });
            }
            State.History = new HistoryPayload
            {
                H5 = Thin(h5, 130),
                Wk = Thin(wk, 130),
                Mo = Thin(mo, 200),
                Total = _hist.Samples.Count,
                Ready = true
            };
        }
        _lastHistSave = DateTime.UtcNow;
    }

    private void RecordLiveUsage()
    {
        var u = State.Usage;
        if (u == null) return;
        double? p5 = u.FiveHour?.Used;
        double? pw = u.Weekly?.Used;
        if (p5 == null && pw == null) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_histLock)
        {
            var last = _hist.Samples.Count > 0 ? _hist.Samples[^1] : null;
            if (last != null && now - last.T < 45 * 1000)
            {
                if (p5.HasValue) last.P5 = p5;
                if (pw.HasValue) last.Pw = pw;
                last.T = now;
            }
            else
            {
                _hist.Samples.Add(new HistSample { T = now, P5 = p5, Pw = pw });
            }
        }
        RefreshHistoryPayload();
        SaveHistoryFile();
    }

    public async Task BackfillHistoryAsync()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - HistKeepMs;
        var batch = new List<HistSample>();
        var changed = false;

        for (var d = 0; d <= 40; d++)
        {
            var dt = DateTime.Now.AddDays(-d);
            var dir = Path.Combine(AppPaths.SessionsDir, dt.Year.ToString(), dt.Month.ToString("00"), dt.Day.ToString("00"));
            if (!Directory.Exists(dir)) continue;

            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            {
                FileInfo st;
                try { st = new FileInfo(f); } catch { continue; }
                var mtime = new DateTimeOffset(st.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (mtime < cutoff) continue;
                lock (_histLock)
                {
                    if (_hist.Scanned.TryGetValue(f, out var prev) && prev.Size == st.Length && Math.Abs(prev.Mtime - mtime) < 1) continue;
                }

                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                foreach (var line in text.Split('\n'))
                {
                    if (!line.Contains("\"used_percent\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        JsonElement rl = default;
                        if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("rate_limits", out var r1)) rl = r1;
                        else if (root.TryGetProperty("rate_limits", out var r2)) rl = r2;
                        if (rl.ValueKind != JsonValueKind.Object) continue;

                        long t = 0;
                        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                            t = dto.ToUnixTimeMilliseconds();
                        if (t == 0 || t < cutoff) continue;

                        double? p5 = null, pw = null;
                        if (rl.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.Object) p5 = Num(pr, "used_percent");
                        if (rl.TryGetProperty("secondary", out var se) && se.ValueKind == JsonValueKind.Object) pw = Num(se, "used_percent");
                        if (p5 == null && pw == null) continue;
                        batch.Add(new HistSample { T = t, P5 = p5, Pw = pw });
                    }
                    catch { }
                }

                lock (_histLock) { _hist.Scanned[f] = new ScanStamp { Size = st.Length, Mtime = mtime }; }
                changed = true;
                await Task.Yield();
            }
        }

        if (changed)
        {
            lock (_histLock)
            {
                _hist.Samples = MergeSamples(_hist.Samples.Concat(batch));
            }
            SaveHistoryFile();
        }
        RefreshHistoryPayload();
        Raise();
    }

    /* ---------------- WorkBuddy ---------------- */

    private static long StartOfDay(long ts)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().Date;
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).ToUnixTimeMilliseconds();
    }

    // 本月计费起点：每月 5 号 00:00（今天还没到 5 号就用上个月的 5 号）
    private static long MonthStartMs()
    {
        var now = DateTime.Now;
        var start = new DateTime(now.Year, now.Month, 5, 0, 0, 0, DateTimeKind.Local);
        if (now < start) start = start.AddMonths(-1);
        return new DateTimeOffset(start).ToUnixTimeMilliseconds();
    }

    // 从会话文件尾部找出「这次对话真正调用过的 skill」。
    // 只能认工具调用行：会话开头的可用技能清单（skills_instructions / developer 消息）、
    // 以及模型思考文本里提到的技能名都不算调用。
    private static List<string> ReadSkills(string path)
    {
        var outList = new List<string>();
        try
        {
            var tail = ReadTail(path, 512 * 1024);
            var lines = tail.Split('\n');
            for (var i = lines.Length - 1; i >= 0 && outList.Count < 4; i--)
            {
                var line = lines[i].Trim();
                if (line.Length < 20 || !line.StartsWith("{")) continue;
                if (line.IndexOf("SKILL.md", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("\"skill\"", StringComparison.Ordinal) < 0) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var kind = Str(root, "type");
                    var toolName = Str(root, "name");
                    if (root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object)
                    {
                        kind = Str(pl, "type") ?? kind;
                        toolName = Str(pl, "name") ?? toolName;
                    }

                    var isTool = kind is "function_call" or "custom_tool_call" or "local_shell_call"
                        or "tool_use" or "tool_call";
                    if (!isTool) continue;

                    var names = new List<string>();
                    if (string.Equals(toolName, "Skill", StringComparison.OrdinalIgnoreCase))
                        foreach (Match m in SkillArgRe.Matches(line)) names.Add(m.Groups[1].Value);
                    foreach (Match m in SkillPathRe.Matches(line)) names.Add(m.Groups[1].Value);

                    for (var k = names.Count - 1; k >= 0; k--)
                    {
                        var n = names[k];
                        if (n.Length < 2 || n.Length > 38) continue;
                        if (n is ".system" or "n" or "n-" or "cache" or "plugins" or "skills") continue;
                        if (outList.Contains(n)) continue;
                        outList.Add(n);
                        if (outList.Count >= 4) break;
                    }
                }
            }
        }
        catch { }
        return outList;
    }

    // WorkBuddy 的模型名在 providerData 里（model / requestModelName / agent）
    private static (string Model, string Label, string Agent, long? At) ReadWbModel(string path)
    {
        string model = null, label = null, agent = null;
        long? at = null;
        try
        {
            var tail = ReadTail(path, 256 * 1024);
            var lines = tail.Split('\n');
            for (var i = lines.Length - 1; i >= 0 && model == null; i--)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("{") || line.IndexOf("\"model\"", StringComparison.Ordinal) < 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("providerData", out var pd) || pd.ValueKind != JsonValueKind.Object) continue;
                    model = Str(pd, "model");
                    if (string.IsNullOrEmpty(model)) continue;
                    label = Str(pd, "requestModelName");
                    at = TsOf(root);
                    if (pd.TryGetProperty("agent", out var ag))
                    {
                        if (ag.ValueKind == JsonValueKind.String) agent = ag.GetString();
                        else if (ag.ValueKind == JsonValueKind.Object) agent = Str(ag, "name");
                    }
                }
            }
        }
        catch { }
        return (model, label, agent, at);
    }

    public void CollectWb()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = now - 8L * 86400000;
        var hourly = new double[24];
        var daily = new double[7];
        var todayStart = StartOfDay(now);
        var dayStarts = new long[7];
        for (var i = 6; i >= 0; i--) dayStarts[6 - i] = StartOfDay(now - i * 86400000L);

        double todayCredit = 0, weekCredit = 0;
        var todayReq = 0;
        var weekReq = 0;
        long lastActive = 0;
        var sessions = new HashSet<string>();
        string newestFile = null;
        long newestMtime = 0;

        if (!Directory.Exists(AppPaths.WbProjects))
        {
            State.Wb = new WbInfo { Hourly = hourly.ToList(), Daily = daily.ToList(), Ready = true };
            _lastWb = DateTime.UtcNow;
            return;
        }

        foreach (var dir in Directory.GetDirectories(AppPaths.WbProjects))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.jsonl"); } catch { continue; }
            foreach (var f in files)
            {
                FileInfo st;
                try { st = new FileInfo(f); } catch { continue; }
                var mtime = new DateTimeOffset(st.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (mtime < cutoff) continue;
                if (mtime > newestMtime) { newestMtime = mtime; newestFile = f; }

                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                var hasToday = false;
                foreach (var line in text.Split('\n'))
                {
                    if (!line.Contains("\"credit\"") && !line.Contains("\"rawUsage\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var t = TsOf(root);
                        if (t == 0 || t < cutoff) continue;
                        if (!root.TryGetProperty("providerData", out var pd) || pd.ValueKind != JsonValueKind.Object) continue;
                        if (!pd.TryGetProperty("rawUsage", out var ru) || ru.ValueKind != JsonValueKind.Object) continue;
                        var credit = Num(ru, "credit");
                        if (credit == null) continue;

                        if (t > lastActive) lastActive = t;
                        if (t >= todayStart) { todayCredit += credit.Value; todayReq++; hasToday = true; }
                        if (t >= now - 7L * 86400000) { weekCredit += credit.Value; weekReq++; }

                        var hIdx = 23 - (int)((now - t) / 3600000);
                        if (hIdx >= 0 && hIdx < 24) hourly[hIdx] += credit.Value;

                        for (var i = 0; i < 7; i++)
                        {
                            if (t >= dayStarts[i]) { daily[i] += credit.Value; break; }
                        }
                    }
                    catch { }
                }
                if (hasToday) sessions.Add(f);
            }
        }

        string wbModel = null, wbLabel = null, wbAgent = null;
        long? wbModelAt = null;
        if (newestFile != null)
        {
            var m = ReadWbModel(newestFile);
            wbModel = m.Model;
            wbLabel = m.Label;
            wbAgent = m.Agent;
            wbModelAt = m.At;
        }

        State.Wb = new WbInfo
        {
            Model = wbModel,
            ModelLabel = wbLabel,
            Agent = wbAgent,
            ModelAt = wbModelAt,
            Running = newestMtime > 0 && now - newestMtime < 25 * 1000,
            TodayCredit = todayCredit,
            WeekCredit = weekCredit,
            TodayReq = todayReq,
            WeekReq = weekReq,
            Sessions = sessions.Count,
            LastActiveAt = lastActive > 0 ? lastActive : null,
            Hourly = hourly.ToList(),
            Daily = daily.ToList(),
            Skills = newestFile != null ? ReadSkills(newestFile) : new List<string>(),
            Ready = true
        };
        _lastWb = DateTime.UtcNow;
    }

    /* ---------------- agent 完成检测 ---------------- */

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string HomeDir() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static long MtimeMs(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond; }
        catch { return 0; }
    }

    private static string EffortZh(string e) => e switch
    {
        null or "" or "none" => "",
        "minimal" => "最低",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "超高",
        "max" => "最高",
        _ => e
    };

    // 与前端 modelText 保持一致的写法：gpt-6.1-sol + medium → "6.1 sol 中"
    private static string ModelText(string model, string effort)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(model))
        {
            var m = Regex.Replace(model, "^openai[-_ ]?", "", RegexOptions.IgnoreCase);
            m = Regex.Replace(m, "^gpt[-_ ]?", "", RegexOptions.IgnoreCase);
            m = m.Replace('-', ' ').Replace('_', ' ').Trim();
            if (m.Length > 0) parts.Add(m);
        }
        var e = EffortZh(effort);
        if (e.Length > 0) parts.Add(e);
        return string.Join(" ", parts);
    }

    private static long? JsonLong(string line, string name)
    {
        var m = Regex.Match(line, "\"" + name + "\"\\s*:\\s*(\\d{9,16})");
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static string JsonLastStr(string line, string name)
    {
        var ms = Regex.Matches(line, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
        return ms.Count > 0 ? ms[ms.Count - 1].Groups[1].Value : null;
    }

    // 该轮 conversation 里有没有工具调用（用于区分「真干活」和「纯聊天」）
    private static bool HasToolCall(string path, string convId, int bytes)
    {
        try
        {
            foreach (var line in ReadTail(path, bytes).Split('\n'))
            {
                if (line.IndexOf("\"function_call\"", StringComparison.Ordinal) < 0) continue;
                if (line.Contains(convId, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    private void FireDone(string agent, string label, long elapsedMs)
    {
        _doneSeq++;
        _doneEvents.Insert(0, new DoneEvent
        {
            Agent = agent,
            Seq = _doneSeq,
            At = NowMs(),
            Label = string.IsNullOrWhiteSpace(label) ? null : label,
            ElapsedMs = elapsedMs > 0 ? elapsedMs : 0
        });
        while (_doneEvents.Count > 12) _doneEvents.RemoveAt(_doneEvents.Count - 1);
        State.Done = new List<DoneEvent>(_doneEvents);
        State.DoneSeq = _doneSeq;
        Log.Write($"done {agent} seq={_doneSeq} label={label} elapsed={elapsedMs}");
    }

    // Codex：一轮结束时最后一条事件变成 task_complete / aborted，Running 立刻转 false，
    // 「运行中 → 空闲」这个边沿就是「这一轮做完了」
    private void TrackCodexDone()
    {
        var now = NowMs();
        if (State.Session?.Running == true)
        {
            if (!_cxRunning) { _cxRunning = true; _cxSince = now; }
            return;
        }
        if (!_cxRunning) return;
        _cxRunning = false;
        var ran = now - _cxSince;
        // 太短的抖动（滚动日志、切模型）不算完成
        if (ran < 12000 || now - _cxLastFire < 12000) return;
        _cxLastFire = now;
        var s = State.Session;
        FireDone("codex", s == null ? null : ModelText(s.Model, s.Effort), ran);
    }

    // WorkBuddy：一个任务（同一 conversationRequestId）会有很多条 assistant message，
    // 所以不能见一条就报。规则：最后一条落盘事件是 assistant message、且文件已安静 20 秒，
    // 且这一轮调过工具（纯聊天不打扰），才判定「任务完成」；
    // 最后一行是 function_call（命令还在跑）时永远不报。
    private void PollWbDone()
    {
        _lastWbPoll = NowMs();
        try
        {
            var root = Path.Combine(HomeDir(), ".workbuddy", "projects");
            string newest = null;
            long newestT = 0;
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                    {
                        var t = MtimeMs(f);
                        if (t > newestT) { newestT = t; newest = f; }
                    }
                }
            }
            if (newest == null) return;
            State.WbFile = newest;
            if (State.Wb != null) State.Wb.FilePath = newest;

            var tail = ReadTail(newest, 160 * 1024);
            long? lastMsgTs = null;
            string model = null;
            string convId = null;
            string lastType = null;
            foreach (var line in tail.Split('\n'))
            {
                string t;
                if (line.IndexOf("\"type\":\"message\"", StringComparison.Ordinal) >= 0) t = "message";
                else if (line.IndexOf("\"type\":\"function_call\"", StringComparison.Ordinal) >= 0) t = "function_call";
                else if (line.IndexOf("\"type\":\"function_call_result\"", StringComparison.Ordinal) >= 0) t = "function_call_result";
                else t = null;

                if (t != null) lastType = t;

                if (t == "message")
                {
                    if (line.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) < 0) continue;
                    var ts = JsonLong(line, "timestamp");
                    if (ts == null) continue;
                    lastMsgTs = ts;
                    model = JsonLastStr(line, "model") ?? model;
                    convId = JsonLastStr(line, "conversationRequestId") ?? convId;
                }
            }
            var quiet = NowMs() - newestT;
            // 还在跑：最后一条是待执行的 function_call（命令没回来），或刚刚才写过
            if (State.Wb != null) State.Wb.Running = lastType == "function_call" || quiet < 20000;

            if (lastMsgTs == null) return;

            if (_wbDoneKey == 0) { _wbDoneKey = lastMsgTs.Value; return; }   // 启动只做基线
            if (lastMsgTs.Value <= _wbDoneKey) return;                       // 这条已经报过了

            var isFinalAnswer = lastType == "message" && quiet >= 20000;
            var stuckInTool = lastType == "function_call";                   // 命令还在执行
            var fallbackQuiet = lastType == "function_call_result" && quiet >= 90000;

            if (stuckInTool) return;
            if (!isFinalAnswer && !fallbackQuiet) return;

            // 这一轮是否真的「干了活」（调过工具）。长任务里 160KB 尾部可能全是工具输出，
            // 看不到 function_call，这时扩大窗口再确认一次
            var convHadToolCall = false;
            if (convId != null)
            {
                foreach (var line in tail.Split('\n'))
                {
                    if (line.IndexOf("\"function_call\"", StringComparison.Ordinal) < 0) continue;
                    if (line.Contains(convId, StringComparison.Ordinal)) { convHadToolCall = true; break; }
                }
                if (!convHadToolCall) convHadToolCall = HasToolCall(newest, convId, 1024 * 1024);
            }
            if (!convHadToolCall)
            {
                _wbDoneKey = lastMsgTs.Value;                               // 纯聊天，静默跳过
                Log.Write($"wb skip chat-only conv={convId} msg={lastMsgTs}");
                return;
            }

            _wbDoneKey = lastMsgTs.Value;
            if (NowMs() - lastMsgTs.Value > 15 * 60 * 1000) return;          // 陈年旧账不提示
            FireDone("workbuddy", model?.Replace('-', ' ').Replace('_', ' '), 0);
        }
        catch (Exception ex) { Log.Write("wb poll: " + ex.Message); }
    }

    // Antigravity：transcript 里最后一条带正文的 PLANNER_RESPONSE 就是这轮交给用户的答复
    private void PollAgDone()
    {
        _lastAgPoll = NowMs();
        try
        {
            var root = Path.Combine(HomeDir(), ".gemini", "antigravity", "brain");
            if (!Directory.Exists(root)) return;
            string newest = null;
            long newestT = 0;
            foreach (var conv in Directory.EnumerateDirectories(root))
            {
                var p = Path.Combine(conv, ".system_generated", "logs", "transcript.jsonl");
                if (!File.Exists(p)) continue;
                var t = MtimeMs(p);
                if (t > newestT) { newestT = t; newest = p; }
            }
            if (newest == null) return;
            State.AgFile = newest;

            var tail = ReadTail(newest, 160 * 1024);
            long? step = null;
            foreach (var line in tail.Split('\n'))
            {
                if (line.IndexOf("\"PLANNER_RESPONSE\"", StringComparison.Ordinal) < 0) continue;
                // 只有带 content 的才是最终答复，工具调用/思考里没有 content
                if (line.IndexOf("\"content\":\"", StringComparison.Ordinal) < 0) continue;
                var si = JsonLong(line, "step_index");
                if (si != null) step = si;
            }
            if (step == null) return;

            if (_agDoneKey == 0) { _agDoneKey = step.Value; return; }
            if (step.Value <= _agDoneKey) return;
            _agDoneKey = step.Value;
            if (NowMs() - newestT > 10 * 60 * 1000) return;
            FireDone("antigravity", null, 0);
        }
        catch (Exception ex) { Log.Write("ag poll: " + ex.Message); }
    }

    /* ---------------- 重置预测 ---------------- */

    public async Task FetchForecastAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ForecastUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 codex-pet");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                State.ForecastError = "HTTP_" + (int)res.StatusCode;
                return;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            JsonElement p = root.TryGetProperty("probabilities", out var pb) ? pb : default;

            int? AsInt(double? v) => v.HasValue ? (int?)Math.Round(v.Value) : null;

            State.Forecast = new ForecastInfo
            {
                P24 = AsInt(Num(p, "rounded_24h") ?? Num(p, "model_24h")),
                P48 = AsInt(Num(p, "rounded_48h") ?? Num(p, "model_48h")),
                Raw24 = Num(p, "model_24h"),
                Raw48 = Num(p, "model_48h"),
                LastResetAt = Str(root, "last_reset_at"),
                AgeDays = Num(root, "age_days"),
                Cadence = root.TryGetProperty("cadence", out var cad) ? Num(cad, "recent_median_days") : null,
                Confidence = Str(root, "confidence"),
                UpdatedAt = Str(root, "updated_at"),
                Tease = root.TryGetProperty("tease_signal", out var tz) && tz.TryGetProperty("post", out var post)
                    ? new TeaseInfo { Quote = Str(post, "quote"), At = Str(post, "at") }
                    : null,
                Hint = root.TryGetProperty("hint_copy", out var hc) ? Str(hc, "zh") : null
            };
            State.ForecastError = null;
            State.LastForecastAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastForecast = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            State.ForecastError = "NETWORK";
            Log.Write("forecast: " + ex.Message);
        }
    }

    /* ---------------- Antigravity ---------------- */

    public async Task FetchAgAsync()
    {
        try
        {
            JsonDocument raw = null;
            if (_agPort != null && _agCsrf != null)
            {
                try { raw = await AgStatusAsync(_agPort, _agCsrf); } catch { raw = null; }
            }
            if (raw == null)
            {
                var found = await AgDiscoverAsync();
                foreach (var (port, csrf) in found)
                {
                    try
                    {
                        var body = await AgStatusAsync(port, csrf);
                        if (body != null && body.RootElement.TryGetProperty("userStatus", out _))
                        {
                            _agPort = port;
                            _agCsrf = csrf;
                            raw = body;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (raw == null)
            {
                State.AgError = "NOT_RUNNING";
                _agPort = null;
                _agCsrf = null;
                return;
            }

            var info = NormalizeAg(raw);
            raw.Dispose();

            // 周额度 / 5 小时额度在单独的汇总接口里
            try
            {
                var quota = await AgQuotaAsync(_agPort, _agCsrf);
                if (quota != null)
                {
                    ApplyQuota(info, quota);
                    quota.Dispose();
                }
            }
            catch (Exception ex) { Log.Write("ag quota: " + ex.Message); }

            State.Antigravity = info;
            State.AgError = null;
            State.LastAgAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastAg = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            State.AgError = "NOT_RUNNING";
            Log.Write("ag: " + ex.Message);
        }
    }

    private async Task<JsonDocument> AgStatusAsync(string port, string csrf)
    {
        using var cts = new CancellationTokenSource(2500);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}{AgStatusPath}");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrf);
        using var res = await _agHttp.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        return JsonDocument.Parse(text);
    }

    private async Task<JsonDocument> AgQuotaAsync(string port, string csrf)
    {
        if (string.IsNullOrEmpty(port) || string.IsNullOrEmpty(csrf)) return null;
        using var cts = new CancellationTokenSource(2500);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}{AgQuotaPath}");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrf);
        using var res = await _agHttp.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        return JsonDocument.Parse(text);
    }

    // 额度汇总：每组模型各有一个 weekly 与一个 5h 桶
    private static void ApplyQuota(AgInfo info, JsonDocument quota)
    {
        var root = quota.RootElement;
        JsonElement resp = root.TryGetProperty("response", out var r) ? r : root;
        if (!resp.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return;

        var list = new List<AgGroup>();
        foreach (var g in groups.EnumerateArray())
        {
            var group = new AgGroup
            {
                Name = Str(g, "displayName") ?? "模型",
                Desc = Str(g, "description")
            };
            if (g.TryGetProperty("buckets", out var bs) && bs.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bs.EnumerateArray())
                {
                    var frac = Num(b, "remainingFraction");
                    if (frac == null) continue;
                    var q = new AgQuota
                    {
                        Remaining = (int)Math.Round(Math.Max(0, Math.Min(1, frac.Value)) * 100),
                        ResetAt = Str(b, "resetTime")
                    };
                    switch (Str(b, "window"))
                    {
                        case "weekly": group.Weekly = q; break;
                        case "5h": group.FiveHour = q; break;
                    }
                }
            }
            list.Add(group);
        }
        if (list.Count > 0) info.Groups = list;
    }

    // language_server 实际监听的端口（新版从 12378 改成了 32xx，直接按进程查最可靠）
    private static List<int> LanguageServerPorts()
    {
        var ports = new List<int>();
        try
        {
            var pids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("language_server"))
            {
                try { pids.Add(p.Id); } catch { }
                try { p.Dispose(); } catch { }
            }
            if (pids.Count == 0) return ports;

            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return ports;
            var outp = proc.StandardOutput.ReadToEnd();
            try { proc.WaitForExit(2000); } catch { }

            foreach (var line in outp.Split('\n'))
            {
                if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[4], out var pid) || !pids.Contains(pid)) continue;
                var local = parts[1];
                var idx = local.LastIndexOf(':');
                if (idx <= 0) continue;
                if (int.TryParse(local.Substring(idx + 1), out var port) && port > 0 && !ports.Contains(port))
                    ports.Add(port);
            }
        }
        catch { }
        return ports;
    }

    private async Task<List<(string Port, string Csrf)>> AgDiscoverAsync()
    {
        var candidates = LanguageServerPorts();
        foreach (var p in AgPorts) if (!candidates.Contains(p)) candidates.Add(p);

        var tasks = candidates.Select(async port =>
        {
            try
            {
                using var cts = new CancellationTokenSource(700);
                using var res = await _agHttp.GetAsync($"https://127.0.0.1:{port}/", cts.Token);
                if (!res.IsSuccessStatusCode) return ((string Port, string Csrf)?)null;
                var body = await res.Content.ReadAsStringAsync(cts.Token);
                var m = CsrfRe.Match(body);
                return m.Success ? (port.ToString(), m.Groups[1].Value) : null;
            }
            catch { return null; }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return results.Where(x => x != null).Select(x => x!.Value).ToList();
    }

    private static AgInfo NormalizeAg(JsonDocument raw)
    {
        var root = raw.RootElement;
        JsonElement us = root.TryGetProperty("userStatus", out var u) ? u : default;
        JsonElement ps = us.ValueKind == JsonValueKind.Object && us.TryGetProperty("planStatus", out var p) ? p : default;
        JsonElement pi = ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("planInfo", out var i) ? i : default;

        var models = new List<AgModel>();
        if (us.ValueKind == JsonValueKind.Object &&
            us.TryGetProperty("cascadeModelConfigData", out var mc) &&
            mc.TryGetProperty("clientModelConfigs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                if (!m.TryGetProperty("quotaInfo", out var q) || q.ValueKind != JsonValueKind.Object) continue;
                var frac = Num(q, "remainingFraction");
                if (frac == null) continue;
                var label = ModelSuffixRe.Replace(Str(m, "label") ?? "", "");
                models.Add(new AgModel
                {
                    Label = label,
                    Remaining = (int)Math.Round(frac.Value * 100),
                    ResetAt = Str(q, "resetTime")
                });
            }
        }

        AgModel Pick(string pattern) => models.FirstOrDefault(m => Regex.IsMatch(m.Label ?? "", pattern, RegexOptions.IgnoreCase));
        var chosen = new List<AgModel>();
        var g = Pick("Gemini"); if (g != null) chosen.Add(g);
        var c = Pick("Claude"); if (c != null) chosen.Add(c);
        var o = Pick("GPT|OSS"); if (o != null) chosen.Add(o);
        foreach (var m in models)
        {
            if (chosen.Count >= 3) break;
            if (!chosen.Contains(m)) chosen.Add(m);
        }

        var tier = us.ValueKind == JsonValueKind.Object && us.TryGetProperty("userTier", out var ut) ? Str(ut, "name") : null;

        return new AgInfo
        {
            Plan = Str(pi, "planName") ?? tier ?? "Pro",
            Tier = tier ?? "",
            Models = chosen.Take(3).ToList(),
            Credits = new AgCredits
            {
                Prompt = new CreditPair
                {
                    Left = (long)(Num(ps, "availablePromptCredits") ?? 0),
                    Total = (long)(Num(pi, "monthlyPromptCredits") ?? 0)
                },
                Flow = new CreditPair
                {
                    Left = (long)(Num(ps, "availableFlowCredits") ?? 0),
                    Total = (long)(Num(pi, "monthlyFlowCredits") ?? 0)
                }
            }
        };
    }

    /* ---------------- 会话进度 ---------------- */

    private (string Path, long Mtime)? LatestSession()
    {
        // 会话是按「开始日期」分目录的，恢复的老会话文件仍在旧日期目录里被追加写入，
        // 所以不能只看最近几天的目录，必须全量按修改时间挑最新的那个。
        string best = null, anyBest = null;
        long bestT = 0, anyT = 0;
        try
        {
            if (Directory.Exists(AppPaths.SessionsDir))
            {
                foreach (var f in Directory.EnumerateFiles(AppPaths.SessionsDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        var t = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds();
                        if (t > anyT) { anyT = t; anyBest = f; }
                        if (t <= bestT) continue;
                        // 子代理会话更新很频繁，不能让它抢走「主会话」的位置
                        if (SessionMeta(f).ThreadSource == "subagent") continue;
                        bestT = t;
                        best = f;
                    }
                    catch { }
                }
            }
        }
        catch { }
        if (best == null) { best = anyBest; bestT = anyT; }
        return best == null ? null : (best, bestT);
    }

    private static string ReadHead(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(bytes, (int)Math.Min(int.MaxValue, fs.Length))];
        var n = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static string ReadTail(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var size = fs.Length;
        var start = Math.Max(0, size - bytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[size - start];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private (string Cwd, string ThreadSource, string Nickname, string AgentPath, string Parent) SessionMeta(string path)
    {
        if (_metaCache.TryGetValue(path, out var cached)) return cached;
        string cwd = "", ts = "", nick = "", ap = "", par = "";
        try
        {
            var head = ReadHead(path, 65536);
            string Pick(Regex re)
            {
                var m = re.Match(head);
                if (!m.Success) return "";
                try { return JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\""); } catch { return m.Groups[1].Value; }
            }
            cwd = Pick(CwdRe);
            ts = Pick(ThreadSourceRe);
            nick = Pick(NicknameRe);
            ap = Pick(AgentPathRe);
            par = Pick(ParentRe);
        }
        catch { }
        var meta = (cwd, ts, nick, ap, par);
        _metaCache[path] = meta;
        return meta;
    }


    private static string SessionIdOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 36 ? name.Substring(name.Length - 36) : name;
    }

    // 读取某个会话文件「此刻」在用的模型：从文件末尾往前找最后一次 turn_context / thread_settings_applied
    private static (string Model, string Effort) ReadSessionModel(string path)
    {
        string model = null, effort = null;
        try
        {
            var tail = ReadTail(path, 256 * 1024);
            var lines = tail.Split('\n').Where(l => l.TrimStart().StartsWith("{")).ToArray();
            for (var i = lines.Length - 1; i >= 0 && model == null; i--)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(lines[i]); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var t = Str(root, "type");
                    if (!root.TryGetProperty("payload", out var pl) || pl.ValueKind != JsonValueKind.Object) continue;
                    if (t == "turn_context")
                    {
                        model = Str(pl, "model");
                        if (model != null) effort = Str(pl, "effort");
                    }
                    else if (t == "event_msg" && Str(pl, "type") == "thread_settings_applied" &&
                             pl.TryGetProperty("thread_settings", out var ts) && ts.ValueKind == JsonValueKind.Object)
                    {
                        model = Str(ts, "model");
                        if (model != null && effort == null) effort = Str(ts, "reasoning_effort");
                    }
                }
            }
            if (model == null)
            {
                var ms = ModelRe.Matches(tail);
                for (var i = ms.Count - 1; i >= 0; i--)
                {
                    var v = ms[i].Groups[1].Value;
                    if (v.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) { model = v; break; }
                }
                if (effort == null)
                {
                    var em = EffortRe.Matches(tail);
                    if (em.Count > 0) effort = em[em.Count - 1].Groups[1].Value;
                }
            }

            // 子代理会话常常只在文件头写 settings / collaboration_mode，尾部没有 turn_context
            if (model == null)
            {
                var head = ReadHead(path, 512 * 1024);
                var sm = SettingsRe.Match(head);
                if (sm.Success)
                {
                    model = sm.Groups[1].Value;
                    effort = sm.Groups[2].Value;
                }
                else
                {
                    var ms = ModelRe.Matches(head);
                    for (var i = ms.Count - 1; i >= 0; i--)
                    {
                        var v = ms[i].Groups[1].Value;
                        if (v.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) { model = v; break; }
                    }
                    if (effort == null)
                    {
                        var em = EffortRe.Matches(head);
                        if (em.Count > 0) effort = em[em.Count - 1].Groups[1].Value;
                    }
                }
            }
        }
        catch { }
        return (model, effort);
    }

    // 主会话最近派发过的子代理（45 分钟内活动过的 subagent 会话）
    private (string Path, long Mtime)? LatestSubagent(string parentId, string mainPath)
    {
        string best = null;
        long bestT = 0;
        try
        {
            var since = DateTime.UtcNow.AddMinutes(-45);
            if (!Directory.Exists(AppPaths.SessionsDir)) return null;
            foreach (var f in Directory.EnumerateFiles(AppPaths.SessionsDir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    if (string.Equals(f, mainPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var mtime = File.GetLastWriteTimeUtc(f);
                    if (mtime < since) continue;
                    var m = SessionMeta(f);
                    if (m.ThreadSource != "subagent") continue;
                    if (!string.IsNullOrEmpty(parentId) && !string.IsNullOrEmpty(m.Parent) &&
                        !string.Equals(m.Parent, parentId, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = new DateTimeOffset(mtime, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    if (t > bestT) { bestT = t; best = f; }
                }
                catch { }
            }
        }
        catch { }
        return best == null ? null : (best, bestT);
    }

    private void CollectSession()
    {
        var latest = LatestSession();
        if (latest == null) { State.Session = null; TrackCodexDone(); return; }

        long? ctxWindow = null;
        long? ctxUsed = null;
        string lastType = null;
        string model = null;
        string effort = null;
        int? turnState = null;   // 1 = 这一轮还在跑，0 = 已收尾，null = 尾部没找到回合事件

        try
        {
            var tail = ReadTail(latest.Value.Path, 512 * 1024);
            var lines = tail.Split('\n').Where(l => l.TrimStart().StartsWith("{")).ToArray();
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(lines[i]); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var t = Str(root, "type");
                    JsonElement pl = root.TryGetProperty("payload", out var p) ? p : default;

                    if (model == null && pl.ValueKind == JsonValueKind.Object)
                    {
                        if (t == "turn_context")
                        {
                            model = Str(pl, "model");
                            if (model != null)
                            {
                                effort = Str(pl, "effort");
                                if (effort == null && pl.TryGetProperty("collaboration_mode", out var cm) &&
                                    cm.TryGetProperty("settings", out var se))
                                    effort = Str(se, "reasoning_effort");
                            }
                        }
                        else if (t == "event_msg" && Str(pl, "type") == "thread_settings_applied" &&
                                 pl.TryGetProperty("thread_settings", out var ts) && ts.ValueKind == JsonValueKind.Object)
                        {
                            // 新版 rollout 用 thread_settings_applied 记录当前模型
                            model = Str(ts, "model");
                            if (model != null && effort == null) effort = Str(ts, "reasoning_effort");
                        }
                    }

                    if (ctxWindow == null && t == "event_msg" && pl.ValueKind == JsonValueKind.Object &&
                        Str(pl, "type") == "token_count" && pl.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                    {
                        ctxWindow = (long?)(Num(info, "model_context_window"));
                        if (info.TryGetProperty("last_token_usage", out var lu) && lu.ValueKind == JsonValueKind.Object)
                            ctxUsed = (long?)(Num(lu, "total_tokens"));
                    }

                    // 这一轮到底结束了没有：看最近的 task_started / task_complete。
                    // 不能只看最后一条事件——改配置、恢复会话会追加 thread_settings_applied，
                    // 排在 task_complete 后面，会被误判成「还在跑」
                    if (turnState == null && t == "event_msg" && pl.ValueKind == JsonValueKind.Object)
                    {
                        switch (Str(pl, "type"))
                        {
                            case "task_started": turnState = 1; break;
                            case "task_complete":
                            case "task_aborted":
                            case "task_failed":
                            case "turn_aborted": turnState = 0; break;
                        }
                    }

                    if (lastType == null)
                    {
                        if (t == "event_msg") lastType = Str(pl, "type") ?? t;
                        else if (t == "response_item") lastType = Str(pl, "type") == "message" ? "message" : "response";
                        else lastType = t;
                    }

                    if (ctxWindow != null && ctxUsed != null && lastType != null && model != null && turnState != null) break;
                }
            }
        }
        catch { }

        if (model == null)
        {
            // 兜底：直接在文件尾部找最后一次出现的 model 字段（比扫头部准确，
            // 头部 session_meta 里是「分叉时的旧模型」，容易显示成早就换掉的型号）
            try
            {
                var tail2 = ReadTail(latest.Value.Path, 1024 * 1024);
                var ms = ModelRe.Matches(tail2);
                for (var i = ms.Count - 1; i >= 0; i--)
                {
                    var v = ms[i].Groups[1].Value;
                    if (v.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) && v.Contains('-'))
                    {
                        model = v;
                        break;
                    }
                }
                if (effort == null)
                {
                    var em = EffortRe.Matches(tail2);
                    if (em.Count > 0) effort = em[em.Count - 1].Groups[1].Value;
                }
            }
            catch { }
        }

        if (model == null)
        {
            try
            {
                var head = ReadHead(latest.Value.Path, 200 * 1024);
                var hs = ModelRe.Matches(head);
                for (var i = hs.Count - 1; i >= 0; i--)
                {
                    var v = hs[i].Groups[1].Value;
                    if (v.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) && v.Contains('-'))
                    {
                        model = v;
                        break;
                    }
                }
                if (effort == null)
                {
                    var e = EffortRe.Matches(head);
                    if (e.Count > 0) effort = e[e.Count - 1].Groups[1].Value;
                }
            }
            catch { }
        }

        var meta = SessionMeta(latest.Value.Path);
        var idleMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - latest.Value.Mtime;
        var done = Regex.IsMatch(lastType ?? "", "complete|completed|aborted|failed");
        // 有明确的回合事件就以它为准；10 分钟没有任何写入的「进行中」视为残留（进程被杀了）
        var running = turnState == 1
            ? idleMs < 10 * 60 * 1000
            : turnState == 0
                ? false
                : !done && idleMs < 45 * 1000;

        State.Session = new SessionInfo
        {
            Title = string.IsNullOrEmpty(meta.Cwd) ? "未命名会话" : Path.GetFileName(meta.Cwd.TrimEnd('\\', '/')),
            Cwd = meta.Cwd,
            FilePath = latest.Value.Path,
            Running = running,
            CtxPercent = ctxWindow > 0 && ctxUsed != null ? (int?)Math.Round((double)ctxUsed.Value / ctxWindow.Value * 100) : null,
            CtxUsed = ctxUsed,
            CtxWindow = ctxWindow,
            Model = model,
            Effort = effort,
            // 只有 thread_source=subagent 才算子代理；agent_forked_thread 是用户会话分叉出来的正常会话
            Subagent = meta.ThreadSource == "subagent"
                ? (string.IsNullOrEmpty(meta.Nickname)
                    ? (string.IsNullOrEmpty(meta.AgentPath) ? "子代理" : meta.AgentPath.Split('/').Where(x => x.Length > 0).LastOrDefault())
                    : meta.Nickname)
                : null,
            SubagentKind = string.IsNullOrEmpty(meta.ThreadSource) ? "user" : meta.ThreadSource,
            LastActiveAt = latest.Value.Mtime,
            LastType = lastType,
            Skills = ReadSkills(latest.Value.Path)
        };

        // 主会话里派发的子代理：把它的模型也带上来
        try
        {
            var sid = SessionIdOf(latest.Value.Path);
            var sub = LatestSubagent(sid, latest.Value.Path);
            if (sub != null)
            {
                var sm = ReadSessionModel(sub.Value.Path);
                if (!string.IsNullOrEmpty(sm.Model))
                {
                    var sMeta = SessionMeta(sub.Value.Path);
                    State.Session.SubModel = sm.Model;
                    State.Session.SubEffort = sm.Effort;
                    State.Session.SubNick = string.IsNullOrEmpty(sMeta.Nickname) ? "子代理" : sMeta.Nickname;
                    State.Session.SubLastAt = sub.Value.Mtime;
                    State.Session.SubRunning =
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sub.Value.Mtime < 60 * 1000;
                }
            }
        }
        catch { }

        TrackCodexDone();
    }

    /* ---------------- JSON 小工具 ---------------- */

    private static double? Num(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static string Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static bool Bool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }

    private static long TsOf(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return 0;
        if (ts.ValueKind == JsonValueKind.Number) return (long)ts.GetDouble();
        if (ts.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUnixTimeMilliseconds();
        return 0;
    }

    /* ---------------- 活跃热力图（30 天 × 24 小时） ---------------- */

    private const int HeatDayCount = 30;
    private static readonly JsonSerializerOptions HeatJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    // 每个 rollout 文件的小时聚合，按 size+mtime 判断有没有变，避免每次都重扫几百 MB
    private readonly Dictionary<string, HeatCacheEntry> _heatCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _heatBuilding;
    private DateTime _lastHeatBuild = DateTime.MinValue;

    public Task BuildHeatAsync() => Task.Run(BuildHeat);

    private void LoadHeatCache()
    {
        try
        {
            if (!File.Exists(AppPaths.HeatFile)) return;
            var d = JsonSerializer.Deserialize<HeatCacheFile>(File.ReadAllText(AppPaths.HeatFile), HeatJson);
            if (d?.Files != null)
                foreach (var kv in d.Files)
                    if (kv.Value != null) _heatCache[kv.Key] = kv.Value;
            Log.Write($"heat cache loaded: {_heatCache.Count} files");
        }
        catch (Exception ex) { Log.Write("heat load: " + ex.Message); }
    }

    private void SaveHeatCache()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var d = new HeatCacheFile();
            foreach (var kv in _heatCache) d.Files[kv.Key] = kv.Value;
            File.WriteAllText(AppPaths.HeatFile, JsonSerializer.Serialize(d, HeatJson));
        }
        catch (Exception ex) { Log.Write("heat save: " + ex.Message); }
    }

    private void BuildHeat()
    {
        if (_heatBuilding) return;
        _heatBuilding = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var today = DateTime.Today;
            var day0 = today.AddDays(-(HeatDayCount - 1));
            var cutoff = new DateTimeOffset(day0).ToUnixTimeMilliseconds();

            var hours = new Dictionary<long, double>(4096);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parsed = 0;

            if (Directory.Exists(AppPaths.SessionsDir))
            {
                foreach (var f in Directory.EnumerateFiles(AppPaths.SessionsDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(f); } catch { continue; }
                    if (!fi.Exists || fi.LastWriteTime < day0) continue;   // 30 天前就不再动的会话不可能有窗口内的事件
                    seen.Add(f);

                    if (_heatCache.TryGetValue(f, out var hit) && hit.Size == fi.Length &&
                        hit.Mtime != 0 && Math.Abs(hit.Mtime - fi.LastWriteTimeUtc.Ticks) < 1)
                    {
                        foreach (var kv in hit.Hours)
                            if (long.TryParse(kv.Key, out var hk) && hk >= cutoff)
                                hours[hk] = hours.TryGetValue(hk, out var old) ? old + kv.Value : kv.Value;
                        continue;
                    }

                    var map = ScanHeatFile(f, cutoff);
                    parsed++;
                    _heatCache[f] = new HeatCacheEntry
                    {
                        Size = fi.Length,
                        Mtime = fi.LastWriteTimeUtc.Ticks,
                        Hours = map.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value)
                    };
                    foreach (var kv in map)
                        hours[kv.Key] = hours.TryGetValue(kv.Key, out var old) ? old + kv.Value : kv.Value;
                }
            }

            foreach (var gone in _heatCache.Keys.Where(k => !seen.Contains(k)).ToList()) _heatCache.Remove(gone);

            var grid = new List<List<int>>(HeatDayCount);
            var days = new List<string>(HeatDayCount);
            int max = 0, total = 0, peakD = 0, peakH = 0;
            for (var d = 0; d < HeatDayCount; d++)
            {
                var day = day0.AddDays(d);
                var row = new List<int>(24);
                for (var h = 0; h < 24; h++)
                {
                    var key = new DateTimeOffset(day.AddHours(h)).ToUnixTimeMilliseconds();
                    var v = hours.TryGetValue(key, out var x) ? x : 0;
                    var k = (int)Math.Round(v / 1000.0, MidpointRounding.AwayFromZero);
                    row.Add(k);
                    total += k;
                    if (k > max) { max = k; peakD = d; peakH = h; }
                }
                grid.Add(row);
                days.Add($"{day.Month:00}-{day.Day:00}");
            }

            // 本月（5 号起）累计 token：直接用小时桶累加，热力图本来就覆盖到 29 天前
            var nowLocal = DateTime.Now;
            var monthStart = new DateTime(nowLocal.Year, nowLocal.Month, 5);
            if (nowLocal.Date < monthStart) monthStart = monthStart.AddMonths(-1);
            if (monthStart < day0) monthStart = day0;
            var monthPts = new List<double[]>();
            var acc = 0.0;
            for (var day = monthStart.Date; day <= nowLocal.Date; day = day.AddDays(1))
            {
                var lastHour = day == nowLocal.Date ? nowLocal.Hour : 23;
                for (var hh = 0; hh <= lastHour; hh++)
                {
                    var hkey = new DateTimeOffset(day.AddHours(hh)).ToUnixTimeMilliseconds();
                    if (hours.TryGetValue(hkey, out var hv)) acc += hv;
                }
                var pointTs = day == nowLocal.Date
                    ? NowMs()
                    : new DateTimeOffset(day.AddDays(1)).ToUnixTimeMilliseconds();
                monthPts.Add(new[] { (double)pointTs, Math.Round(acc / 1000.0, 1) });
            }
            var monthTotal = (int)Math.Round(acc / 1000.0);

            var payload = new HeatPayload
            {
                Grid = grid,
                Month = monthPts,
                MonthTotal = monthTotal,
                MonthStart = new DateTimeOffset(monthStart.Date).ToUnixTimeMilliseconds(),
                Days = days,
                Max = max,
                Total = total,
                PeakDay = peakD,
                PeakHour = peakH,
                PeakText = max > 0 ? $"{days[peakD]} {peakH:00}:00 · {FmtK(max)}" : null,
                UpdatedAt = NowMs(),
                Ready = true
            };
            lock (Sync) State.Heat = payload;
            SaveHeatCache();
            _lastHeatBuild = DateTime.UtcNow;
            Log.Write($"heat built: parsed={parsed} cached={_heatCache.Count} max={max}k total={total}k in {sw.ElapsedMilliseconds}ms");
            Raise();
        }
        catch (Exception ex) { Log.Write("heat: " + ex.Message); }
        finally { _heatBuilding = false; }
    }

    private static string FmtK(int k) => k >= 1000
        ? (k / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : k + "k";

    // 一个 rollout 里每个 token_count 事件带的是「本轮消耗」，按本地小时累加就是生成量
    private static Dictionary<long, double> ScanHeatFile(string path, long cutoff)
    {
        var map = new Dictionary<long, double>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var sr = new StreamReader(fs, Encoding.UTF8, false, 1 << 16);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) continue;
                if (line.IndexOf("\"last_token_usage\"", StringComparison.Ordinal) < 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("payload", out var pl)) continue;
                    if (!pl.TryGetProperty("info", out var info)) continue;
                    if (!info.TryGetProperty("last_token_usage", out var lu)) continue;
                    var tok = Num(lu, "output_tokens") ?? 0;
                    if (tok <= 0) continue;
                    var ms = TsOf(root);
                    if (ms < cutoff || ms <= 0) continue;
                    var lt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                    var key = new DateTimeOffset(lt.Year, lt.Month, lt.Day, lt.Hour, 0, 0, lt.Offset).ToUnixTimeMilliseconds();
                    map[key] = map.TryGetValue(key, out var old) ? old + tok : tok;
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Write("heat scan " + Path.GetFileName(path) + ": " + ex.Message); }
        return map;
    }
}
