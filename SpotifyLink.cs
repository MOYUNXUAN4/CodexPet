using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPet;

/// <summary>
/// 把卡片上的这行"正在播放"点进 Spotify。
///
/// 难点：GSMTC 只给歌名/歌手/专辑三段纯文本，不给 Spotify 的 base62 ID，
/// 而 <c>spotify:artist:&lt;id&gt;</c> 只认 ID。所以分三级降级：
///   1. 用 MusicBrainz 反查（歌手的 url-rels 里常有 open.spotify.com/artist/xxx 链接）→ 直达歌手页 / 专辑详情；
///   2. 查不到 → <c>spotify:search:artist:"名字"</c> 这类过滤语法，Spotify 打开后落在正确的结果页；
///   3. 连 spotify: 协议都没注册（没装客户端）→ 退回 open.spotify.com 网页。
///
/// 反查要联网，所以换歌时后台先预热；点下去时若还没查完，最多等 1.2 秒就先用搜索兜底，不让手感受网络牵制。
/// </summary>
public static class SpotifyLink
{
    private const int ResolveWaitMs = 1200;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>同一个文本只查一次（查不到也记住，免得每次点击都去敲 MusicBrainz）。</summary>
    private static readonly ConcurrentDictionary<string, Task<string>> Jobs = new();

    /// <summary>查过就落盘，下次启动同一个歌手/专辑不用再查，第一次点也就不用等。</summary>
    private static readonly object DiskLock = new();
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static Dictionary<string, string[]> _disk;

    /// <summary>MusicBrainz 匿名调用限速 1 次/秒，全局排队。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _lastCall;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CodexPet/1.0 ( desktop dashboard )");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return c;
    }

    /* ---------------- 对外入口 ---------------- */

    /// <summary>换歌时先把歌手/专辑页面查好，点下去就能立刻跳。</summary>
    public static void PreResolve(string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        _ = Job("a\u0001" + Norm(artist), () => ResolveArtistAsync(artist));
        if (!string.IsNullOrWhiteSpace(album))
            _ = Job("b\u0001" + Norm(artist) + "\u0001" + Norm(album), () => ResolveAlbumAsync(artist, album));
    }

    /// <summary>what: track / artist / album。</summary>
    public static void Open(string what, MediaInfo m)
    {
        if (m == null || !m.On) return;

        var title = (m.Title ?? "").Trim();
        var artist = (m.Artist ?? "").Trim();
        var album = (m.Album ?? "").Trim();
        if (title.Length == 0 && artist.Length == 0) return;

        _ = Task.Run(async () =>
        {
            var uri = "";
            try
            {
                if (what == "artist" && artist.Length > 0)
                {
                    uri = await Within(Job("a\u0001" + Norm(artist), () => ResolveArtistAsync(artist))).ConfigureAwait(false);
                    if (uri.Length == 0) uri = SearchUri("artist:" + Quote(artist));
                }
                else if (what == "album" && album.Length > 0)
                {
                    uri = await Within(Job("b\u0001" + Norm(artist) + "\u0001" + Norm(album),
                        () => ResolveAlbumAsync(artist, album))).ConfigureAwait(false);
                    if (uri.Length == 0)
                    {
                        var q = "album:" + Quote(album);
                        if (artist.Length > 0) q += " artist:" + Quote(artist);
                        uri = SearchUri(q);
                    }
                }
                else
                {
                    // 歌本身：没有 ID 可查，搜歌名 + 歌手，结果页第一条就是它
                    var q = title.Length > 0 ? "track:" + Quote(title) : "";
                    if (artist.Length > 0) q += (q.Length > 0 ? " " : "") + "artist:" + Quote(artist);
                    if (q.Length == 0) q = Quote(album);
                    uri = SearchUri(q);
                }
            }
            catch (Exception ex) { Log.Write("spotify link: " + ex.Message); }

            if (uri.Length == 0) return;
            Launch(uri);
        });
    }

    /* ---------------- 跳转 ---------------- */

    private static void Launch(string uri)
    {
        // 我们把前台权让出去，否则已经在跑的 Spotify 只能在自己的窗口里默默换页，不会跳到最前
        try { AllowSetForegroundWindow(0xFFFFFFFF); } catch { }

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            Log.Write("spotify open " + uri);
            _ = Task.Delay(280).ContinueWith(_ => Activate());
            return;
        }
        catch (Exception ex)
        {
            Log.Write("spotify open fail: " + ex.Message);
        }

        // 没装客户端：退回网页
        try
        {
            const string prefix = "spotify:search:";
            var web = uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? "https://open.spotify.com/search/" + uri.Substring(prefix.Length)
                : "https://open.spotify.com/";
            Process.Start(new ProcessStartInfo(web) { UseShellExecute = true });
            Log.Write("spotify web fallback " + web);
        }
        catch (Exception ex) { Log.Write("spotify web fail: " + ex.Message); }
    }

    /// <summary>把 Spotify 主窗口调到前面（客户端收到 URI 不一定会自己抢焦点）。</summary>
    private static void Activate()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Spotify"))
            {
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                break;
            }
        }
        catch { }
    }

    /* ---------------- 收藏到「我喜欢」 ---------------- */

    /// <summary>
    /// 收藏当前曲目：转按 Spotify 桌面版自己的快捷键 Alt + Shift + B。
    /// 这是官方文档里写着的「Like / Save current song」，按下去它自己的播放条会出现绿色对勾，
    /// 但它是开关（再按一次取消），而 GSMTC 读不到"是否已收藏"，所以卡片上只能记点击次数。
    /// </summary>
    public static bool SendLike()
    {
        var hwnd = SpotifyWindow();
        if (hwnd == IntPtr.Zero)
        {
            Log.Write("like: spotify window not found");
            return false;
        }

        var back = GetForegroundWindow();
        try
        {
            // 焦点没抢到就别按键了 —— 不然这串组合键会打进别人正在打字的窗口里
            if (!Focus(hwnd))
            {
                Log.Write("like: not foreground, skipped");
                return false;
            }

            Tap(VK_MENU, false);
            Tap(VK_SHIFT, false);
            Tap(VK_B, false);
            Thread.Sleep(30);
            Tap(VK_B, true);
            Tap(VK_SHIFT, true);
            Tap(VK_MENU, true);
            Thread.Sleep(180);

            // 按完把前台还回去，别把人从正在打字的窗口里拽出来不放
            try
            {
                if (back != IntPtr.Zero && back != hwnd && back != GetForegroundWindow())
                    SetForegroundWindow(back);
            }
            catch { }

            Log.Write("like: sent alt+shift+b");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("like fail: " + ex.Message);
            return false;
        }
    }

    /// <summary>把窗口推到前台。前台锁会拦跨进程的 SetForegroundWindow，用 AttachThreadInput 绕过去。</summary>
    private static bool Focus(IntPtr hwnd)
    {
        for (var i = 0; i < 4; i++)
        {
            var attached = false;
            var fgThread = 0u;
            var myThread = GetCurrentThreadId();
            try
            {
                try { AllowSetForegroundWindow(0xFFFFFFFF); } catch { }
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    uint pid;
                    fgThread = GetWindowThreadProcessId(fg, out pid);
                    if (fgThread != 0 && fgThread != myThread)
                        attached = AttachThreadInput(myThread, fgThread, true);
                }
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            catch { }
            finally
            {
                if (attached)
                {
                    try { AttachThreadInput(myThread, fgThread, false); } catch { }
                }
            }

            Thread.Sleep(130);
            if (GetForegroundWindow() == hwnd) return true;
        }
        return false;
    }

    private static IntPtr SpotifyWindow()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Spotify"))
            {
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
        }
        catch { }

        // 最小化到托盘时 MainWindowHandle 可能为 0，按进程 + 窗口类名再找一遍
        var found = IntPtr.Zero;
        var pids = new HashSet<uint>();
        try
        {
            foreach (var p in Process.GetProcessesByName("Spotify")) pids.Add((uint)p.Id);
        }
        catch { return IntPtr.Zero; }
        if (pids.Count == 0) return IntPtr.Zero;

        try
        {
            EnumWindows((h, _) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;   // 主窗口就是它
                found = h;
                return false;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    private static void Tap(ushort vk, bool up)
    {
        var input = new INPUT { type = 1 };
        input.u.ki.wVk = vk;
        input.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(28);
    }

    /* ---------------- MusicBrainz 反查 ---------------- */

    private static Task<string> Job(string key, Func<Task<string>> work)
    {
        var known = DiskGet(key);
        if (known != null) return Task.FromResult(known);

        return Jobs.GetOrAdd(key, _ => Task.Run(async () =>
        {
            var uri = "";
            try { uri = await work().ConfigureAwait(false) ?? ""; } catch { }
            Remember(key, uri);
            return uri;
        }));
    }

    private static string DiskPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "codex-pet", "spotify-ids.json");

    private static string DiskGet(string key)
    {
        try
        {
            lock (DiskLock)
            {
                LoadDisk();
                if (!_disk.TryGetValue(key, out var row) || row == null || row.Length < 2) return null;
                if (!long.TryParse(row[1], out var ts)) return null;
                if ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > MaxAge.TotalSeconds) return null;
                return row[0] ?? "";
            }
        }
        catch { return null; }
    }

    private static void Remember(string key, string uri)
    {
        try
        {
            lock (DiskLock)
            {
                LoadDisk();
                _disk[key] = new[] { uri ?? "", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() };
                var dir = Path.GetDirectoryName(DiskPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(DiskPath, JsonSerializer.Serialize(_disk));
            }
        }
        catch (Exception ex) { Log.Write("spotify cache: " + ex.Message); }
    }

    private static void LoadDisk()
    {
        if (_disk != null) return;
        _disk = new Dictionary<string, string[]>();
        try
        {
            if (!File.Exists(DiskPath)) return;
            var txt = File.ReadAllText(DiskPath);
            if (string.IsNullOrWhiteSpace(txt)) return;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(txt);
            if (parsed != null) _disk = parsed;
        }
        catch { _disk = new Dictionary<string, string[]>(); }
    }

    /// <summary>最多等这么久，查不完就先拿搜索兜底（后台仍会补上缓存）。</summary>
    private static async Task<string> Within(Task<string> job)
    {
        var done = await Task.WhenAny(job, Task.Delay(ResolveWaitMs)).ConfigureAwait(false);
        if (done != job) return "";
        try { return job.Result ?? ""; } catch { return ""; }
    }

    private static async Task<string> ResolveArtistAsync(string artist)
    {
        try
        {
            var json = await GetJsonAsync(
                "https://musicbrainz.org/ws/2/artist/?fmt=json&limit=5&query=artist%3A%22" +
                Uri.EscapeDataString(artist) + "%22").ConfigureAwait(false);
            if (json == null) return "";

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("artists", out var list) || list.ValueKind != JsonValueKind.Array)
                return "";

            var want = Norm(artist);
            foreach (var a in list.EnumerateArray())
            {
                var name = Str(a, "name");
                // 名字必须基本一致，宁可退回搜索也别跳错人
                if (want.Length == 0 || Norm(name) != want) continue;

                var id = Str(a, "id");
                if (id.Length == 0) continue;

                var rels = await GetJsonAsync(
                    "https://musicbrainz.org/ws/2/artist/" + id + "?fmt=json&inc=url-rels").ConfigureAwait(false);
                var sp = SpotifyIdFromRels(rels, "artist");
                if (sp.Length > 0) return "spotify:artist:" + sp;
            }
        }
        catch (Exception ex) { Log.Write("mb artist: " + ex.Message); }
        return "";
    }

    private static async Task<string> ResolveAlbumAsync(string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        try
        {
            var q = "https://musicbrainz.org/ws/2/release/?fmt=json&limit=8&query=release%3A%22" +
                    Uri.EscapeDataString(album) + "%22%20AND%20artist%3A%22" + Uri.EscapeDataString(artist) + "%22";
            var json = await GetJsonAsync(q).ConfigureAwait(false);
            if (json == null) return "";

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("releases", out var list) || list.ValueKind != JsonValueKind.Array)
                return "";

            var wantAlbum = Norm(album);
            var wantArtist = Norm(artist);

            foreach (var r in list.EnumerateArray())
            {
                if (Norm(Str(r, "title")) != wantAlbum) continue;
                if (!CreditHas(r, wantArtist)) continue;

                var id = Str(r, "id");
                if (id.Length == 0) continue;

                var rels = await GetJsonAsync(
                    "https://musicbrainz.org/ws/2/release/" + id + "?fmt=json&inc=url-rels").ConfigureAwait(false);
                var sp = SpotifyIdFromRels(rels, "album");
                if (sp.Length > 0) return "spotify:album:" + sp;
            }
        }
        catch (Exception ex) { Log.Write("mb album: " + ex.Message); }
        return "";
    }

    /// <summary>release.artist-credit 里挂着这首歌的歌手（可能有 feat.，所以用包含判断）。</summary>
    private static bool CreditHas(JsonElement release, string wantArtist)
    {
        if (wantArtist.Length == 0) return true;
        try
        {
            if (!release.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var c in credit.EnumerateArray())
            {
                var n = Norm(Str(c, "name"));
                if (n.Length == 0 && c.TryGetProperty("artist", out var ar)) n = Norm(Str(ar, "name"));
                if (n.Length == 0) continue;
                if (n == wantArtist || n.Contains(wantArtist) || wantArtist.Contains(n)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string SpotifyIdFromRels(string json, string kind)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array)
                return "";
            foreach (var r in rels.EnumerateArray())
            {
                if (!r.TryGetProperty("url", out var u)) continue;
                var res = Str(u, "resource");
                if (res.IndexOf("open.spotify.com/" + kind + "/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var id = SpotifyId(res, kind);
                if (id.Length > 0) return id;
            }
        }
        catch { }
        return "";
    }

    private static string SpotifyId(string url, string kind)
    {
        var marker = "open.spotify.com/" + kind + "/";
        var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var rest = url.Substring(i + marker.Length);
        var cut = rest.IndexOfAny(new[] { '?', '#', '/' });
        if (cut >= 0) rest = rest.Substring(0, cut);
        return rest.Length >= 10 ? rest : "";
    }

    private static async Task<string> GetJsonAsync(string url)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wait = 1100 - (Environment.TickCount64 - _lastCall);
            if (wait > 0) await Task.Delay((int)wait).ConfigureAwait(false);
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            finally { _lastCall = Environment.TickCount64; }
        }
        catch (Exception ex)
        {
            Log.Write("mb http: " + ex.Message);
            return null;
        }
        finally { Gate.Release(); }
    }

    /* ---------------- 小工具 ---------------- */

    /// <summary>spotify:search: 后面跟过滤语法，整段做百分号编码（和实测能用的形式一致）。</summary>
    private static string SearchUri(string query) => "spotify:search:" + Uri.EscapeDataString(query);

    private static string Quote(string s) => "\"" + s.Replace("\"", "").Replace("\r", " ").Replace("\n", " ").Trim() + "\"";

    /// <summary>只留字母数字（含中文），用来做"是不是同一张专辑/同一个人"的宽松比对。</summary>
    private static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string Str(JsonElement e, string name)
    {
        try
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private const int SW_RESTORE = 9;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_B = 0x42;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
