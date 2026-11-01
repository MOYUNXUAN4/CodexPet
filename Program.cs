using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CodexPet;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        Application.ThreadException += (_, e) => Log.Write("ui-thread: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled: " + e.ExceptionObject);

        var cfg = ConfigStore.Load();

        // 开发自检：不开窗口，直接拿当前正在播的这首去试跳转（--open-link artist）
        var li = Array.IndexOf(args, "--open-link");
        if (li >= 0 && li + 1 < args.Length)
        {
            MediaWatcher.Shared.Start();
            MediaInfo cur = null;
            for (var i = 0; i < 25 && cur == null; i++)
            {
                Thread.Sleep(200);
                var c = MediaWatcher.Shared.Current;
                if (c.On && c.Title.Length > 0) cur = c;
            }
            Log.Write("link-test: " + (cur == null ? "no media" : cur.Artist + " | " + cur.Album + " | " + cur.Title));
            if (cur != null)
            {
                if (args[li + 1] == "like")
                {
                    // 连按两次 = 收藏再取消，验证完不改动曲目的收藏状态
                    Log.Write("like-test1=" + SpotifyLink.SendLike());
                    Thread.Sleep(1400);
                    Log.Write("like-test2=" + SpotifyLink.SendLike());
                    Thread.Sleep(900);
                }
                else
                {
                    SpotifyLink.Open(args[li + 1], cur);
                    Thread.Sleep(4500);
                }
            }
            return;
        }

        if (args.Contains("--autostart-on")) Log.Write("autostart-on=" + Shortcuts.SetAutoStart(true));
        if (args.Contains("--autostart-off")) Log.Write("autostart-off=" + Shortcuts.SetAutoStart(false));
        if (args.Contains("--desktop-shortcut")) Log.Write("desktop-shortcut=" + Shortcuts.CreateDesktop());

        string capture = null;
        var ci = Array.IndexOf(args, "--capture");
        if (ci >= 0 && ci + 1 < args.Length) capture = args[ci + 1];

        string dump = null;
        var di = Array.IndexOf(args, "--dump");
        if (di >= 0 && di + 1 < args.Length) dump = args[di + 1];

        // 调试导出允许与正在运行的实例并存，导出后自行退出
        using var mutex = new Mutex(true, "CodexPetLite.SingleInstance", out var isNew);
        if (!isNew && dump == null) return;

        var data = new DataService(cfg);
        Application.Run(new MainForm(data, cfg, capture, dump));
    }
}
