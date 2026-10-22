using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexPet;

public static class Shortcuts
{
    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string WorkDir => AppContext.BaseDirectory;

    public static bool AutoStartEnabled()
    {
        try { return File.Exists(AppPaths.StartupLnk); } catch { return false; }
    }

    public static bool SetAutoStart(bool on)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.StartupDir);
            var legacy = Path.Combine(AppPaths.StartupDir, "Codex 仪表盘.vbs");
            if (File.Exists(legacy)) File.Delete(legacy);

            if (!on)
            {
                if (File.Exists(AppPaths.StartupLnk)) File.Delete(AppPaths.StartupLnk);
                return false;
            }
            return CreateLnk(AppPaths.StartupLnk, "Codex 额度仪表盘（开机自动启动）");
        }
        catch (Exception ex) { Log.Write("autostart: " + ex.Message); return AutoStartEnabled(); }
    }

    public static bool CreateDesktop() => CreateLnk(AppPaths.DesktopLnk, "Codex 额度仪表盘");

    private static bool CreateLnk(string path, string description)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return false;
            dynamic shell = Activator.CreateInstance(type);
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = ExePath;
            lnk.Arguments = "";
            lnk.WorkingDirectory = WorkDir;
            lnk.Description = description;
            lnk.IconLocation = ExePath + ",0";
            lnk.Save();
            Marshal.ReleaseComObject(lnk);
            Marshal.ReleaseComObject(shell);
            return File.Exists(path);
        }
        catch (Exception ex) { Log.Write("shortcut: " + ex.Message); return false; }
    }
}
