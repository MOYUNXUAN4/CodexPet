using System;
using System.IO;
using System.Reflection;
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

    // 创建 .lnk。这里刻意不用 dynamic：程序开了 InvariantGlobalization，
    // dynamic 的运行时绑定器会去取 CultureInfo，抛 CultureNotFoundException，
    // 表现就是「点了创建快捷方式没反应、启动目录里也始终没有快捷方式」。
    // 换成反射 InvokeMember 就完全绕开这个坑。
    private static bool CreateLnk(string path, string description)
    {
        object shell = null, lnk = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) { Log.Write("shortcut: WScript.Shell 不可用"); return false; }

            shell = Activator.CreateInstance(type);
            lnk = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (lnk == null) { Log.Write("shortcut: CreateShortcut 返回 null"); return false; }

            var lt = lnk.GetType();
            void Set(string name, string value) =>
                lt.InvokeMember(name, BindingFlags.SetProperty, null, lnk, new object[] { value });

            Set("TargetPath", ExePath);
            Set("Arguments", "");
            Set("WorkingDirectory", WorkDir);
            Set("Description", description);
            Set("IconLocation", ExePath + ",0");
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);

            var ok = File.Exists(path);
            Log.Write("shortcut " + (ok ? "ok " : "fail ") + path + " -> " + ExePath);
            return ok;
        }
        catch (Exception ex) { Log.Write("shortcut: " + ex.Message); return false; }
        finally
        {
            if (lnk != null) { try { Marshal.ReleaseComObject(lnk); } catch { } }
            if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }
        }
    }
}
