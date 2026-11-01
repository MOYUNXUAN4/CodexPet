using System;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CodexPet;

/// <summary>一次播放状态的快照，直接序列化给前端。</summary>
public sealed class MediaInfo
{
    public bool On { get; set; }
    public string App { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    /// <summary>专辑封面的 data URI；只有换歌时才带内容，其余时候是空串（前端沿用旧图）。</summary>
    public string Cover { get; set; } = "";
    public double Pos { get; set; }
    public double Dur { get; set; }
    public bool Playing { get; set; }
    public bool CanPrev { get; set; }
    public bool CanNext { get; set; }
    /// <summary>取样时刻（本地毫秒），前端据此做进度插值。</summary>
    public long At { get; set; }

    public MediaInfo Clone() => (MediaInfo)MemberwiseClone();
}

/// <summary>
/// 系统媒体会话（GSMTC）轮询器：任何向 Windows 上报播放信息的播放器都能读到，
/// Spotify 桌面版就在其中。控制指令也走同一套接口，不用 Spotify API 授权。
/// </summary>
public sealed class MediaWatcher
{
    public static readonly MediaWatcher Shared = new();

    private const int PollMs = 700;
    private const int SyncMs = 2200;   // 内容没变时，隔一会儿校准一次进度，防止前端插值漂

    private readonly object _sync = new();
    private MediaInfo _cur = new() { On = false, At = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
    private Thread _thread;
    private volatile bool _stop;
    private GlobalSystemMediaTransportControlsSessionManager _mgr;
    private string _coverKey;
    private string _cover = "";
    private long _lastPush;
    private bool _forcePush;
    private string _lastError;

    /// <summary>状态有变化时在轮询线程上触发，订阅方自己切回 UI 线程。</summary>
    public event Action<MediaInfo> Changed;

    public MediaInfo Current { get { lock (_sync) return _cur.Clone(); } }

    public void Stop() => _stop = true;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "media-poll" };
        try { _thread.SetApartmentState(ApartmentState.MTA); } catch { }
        _thread.Start();
    }

    private static long Now => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private void Loop()
    {
        long backoffUntil = 0;
        while (!_stop)
        {
            try
            {
                if (Now >= backoffUntil)
                {
                    if (_mgr == null)
                    {
                        _mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                            .AsTask().GetAwaiter().GetResult();
                        Log.Write("media: manager ready");
                    }
                    Poll();
                }
            }
            catch (Exception ex)
            {
                // 媒体服务偶发拒绝访问（比如播放器刚重启），退避后重来，不要刷日志
                if (ex.Message != _lastError) { Log.Write("media poll: " + ex.Message); _lastError = ex.Message; }
                _mgr = null;
                backoffUntil = Now + 5000;
            }
            Thread.Sleep(PollMs);
        }
    }

    /// <summary>挑一个"最该显示"的会话：正在播的优先，其中 Spotify 优先；都没有就退回当前会话。</summary>
    private GlobalSystemMediaTransportControlsSession Pick()
    {
        GlobalSystemMediaTransportControlsSession spotify = null;
        GlobalSystemMediaTransportControlsSession playing = null;
        GlobalSystemMediaTransportControlsSession any = null;

        try
        {
            var list = _mgr.GetSessions();
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null) continue;
                any ??= s;
                var id = s.SourceAppUserModelId ?? "";
                var isSpotify = id.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSpotify) spotify ??= s;

                var isPlaying = false;
                try
                {
                    isPlaying = s.GetPlaybackInfo().PlaybackStatus ==
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
                catch { }
                if (isPlaying)
                {
                    if (isSpotify) return s;    // 最理想：Spotify 正在放
                    playing ??= s;
                }
            }
        }
        catch { }

        if (playing != null) return playing;

        try
        {
            var cur = _mgr.GetCurrentSession();
            if (cur != null) return cur;
        }
        catch { }

        return spotify ?? any;
    }

    private void Poll()
    {
        var s = Pick();
        if (s == null)
        {
            Publish(new MediaInfo { On = false, At = Now }, false);
            return;
        }

        var info = new MediaInfo { On = true, At = Now };

        try
        {
            var id = s.SourceAppUserModelId ?? "";
            info.App = id.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0 ? "Spotify" : PrettyApp(id);

            var props = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            if (props != null)
            {
                info.Title = (props.Title ?? "").Trim();
                info.Artist = (props.Artist ?? "").Trim();
                info.Album = (props.AlbumTitle ?? "").Trim();
                if (info.Artist.Length == 0) info.Artist = (props.AlbumArtist ?? "").Trim();
                if (info.Title.Length == 0) info.Title = info.Album;

                var key = info.Title + "\u0001" + info.Artist + "\u0001" + info.Album;
                if (key != _coverKey)
                {
                    _coverKey = key;
                    _cover = LoadCover(props.Thumbnail);
                }
                info.Cover = _cover;
            }
        }
        catch (Exception ex)
        {
            // 换歌的瞬间属性会短暂拿不到，沿用上一次的文本，别闪成空
            info.Title = _cur.Title;
            info.Artist = _cur.Artist;
            info.Album = _cur.Album;
            info.Cover = _cur.Cover;
            if (!string.IsNullOrEmpty(ex.Message)) _lastError = ex.Message;
        }

        try
        {
            var pi = s.GetPlaybackInfo();
            info.Playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var c = pi.Controls;
            if (c != null)
            {
                info.CanPrev = c.IsPreviousEnabled;
                info.CanNext = c.IsNextEnabled;
            }
        }
        catch { }

        try
        {
            var tl = s.GetTimelineProperties();
            info.Pos = tl.Position.TotalSeconds;
            info.Dur = tl.EndTime.TotalSeconds;
            if (info.Dur <= 0) info.Dur = tl.MaxSeekTime.TotalSeconds;
            if (info.Dur > 0 && info.Pos > info.Dur) info.Pos = info.Dur;
            if (info.Pos < 0) info.Pos = 0;
        }
        catch { }

        Publish(info, false);
    }

    private void Publish(MediaInfo info, bool force)
    {
        lock (_sync)
        {
            var old = _cur;
            var trackChanged =
                old.Title != info.Title || old.Artist != info.Artist ||
                old.Album != info.Album || old.App != info.App;

            var contentChanged = trackChanged ||
                old.On != info.On || old.Playing != info.Playing ||
                Math.Abs(old.Dur - info.Dur) > 0.5;

            // 封面只在实际换歌时下发，省得每次轮询都搬几十 KB 的 base64
            if (!trackChanged) info.Cover = "";

            var since = Now - _lastPush;
            if (!contentChanged && !force && !_forcePush && since < SyncMs) return;
            if (!contentChanged && !force && since < PollMs) return;

            _forcePush = false;
            _lastPush = Now;
            _cur = info.Clone();
            info = _cur.Clone();
        }

        try { Changed?.Invoke(info); } catch (Exception ex) { Log.Write("media event: " + ex.Message); }
    }

    private static string PrettyApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "媒体";
        var s = appId;
        var bang = s.IndexOf('!');
        if (bang > 0) s = s.Substring(0, bang);
        var slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s.Substring(slash + 1);
        var dot = s.LastIndexOf('.');
        if (dot > 0) s = s.Substring(0, dot);
        return s.Length == 0 ? "媒体" : s;
    }

    private static string LoadCover(IRandomAccessStreamReference thumb)
    {
        if (thumb == null) return "";
        try
        {
            using var ras = thumb.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            if (ras == null) return "";
            var size = (long)ras.Size;
            if (size <= 0 || size > 4 * 1024 * 1024) return "";
            var buf = new byte[size];
            using (var reader = new DataReader(ras.GetInputStreamAt(0)))
            {
                var loaded = reader.LoadAsync((uint)size).AsTask().GetAwaiter().GetResult();
                if (loaded < size) return "";
                reader.ReadBytes(buf);
            }
            var png = buf.Length > 3 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47;
            return "data:" + (png ? "image/png" : "image/jpeg") + ";base64," + Convert.ToBase64String(buf);
        }
        catch { return ""; }
    }

    /* ---------------- 控制指令 ---------------- */

    private bool Run(string tag, Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> act)
    {
        try
        {
            var s = Pick();
            if (s == null) return false;
            var task = act(s).AsTask();
            if (!task.Wait(2000)) { Log.Write("media " + tag + ": timeout"); return false; }
            var ok = task.Result;
            _forcePush = true;
            Log.Write("media " + tag + " -> " + ok);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write("media " + tag + " fail: " + ex.Message);
            return false;
        }
    }

    public bool TogglePlayPause() => Run("toggle", s => s.TryTogglePlayPauseAsync());

    public bool Next() => Run("next", s => s.TrySkipNextAsync());

    public bool Prev() => Run("prev", s => s.TrySkipPreviousAsync());

    public bool Seek(double seconds)
    {
        var ticks = (long)Math.Round(Math.Max(0, seconds) * 10_000_000d);
        return Run("seek", s => s.TryChangePlaybackPositionAsync(ticks));
    }
}
