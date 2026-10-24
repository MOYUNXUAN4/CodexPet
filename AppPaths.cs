using System;
using System.IO;
using System.Text.Json;

namespace CodexPet;

public static class AppPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string DataDir => Path.Combine(AppData, "codex-pet");
    public static string ConfigFile => Path.Combine(DataDir, "codex-pet-config.json");
    public static string HistoryFile => Path.Combine(DataDir, "codex-history.json");
    // 热力图的增量缓存：每个 rollout 文件已按小时聚合过的结果
    public static string HeatFile => Path.Combine(DataDir, "codex-heat.json");
    public static string StartupDir => Path.Combine(AppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    public static string StartupLnk => Path.Combine(StartupDir, "Codex 仪表盘.lnk");
    public static string DesktopLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Codex 仪表盘.lnk");
    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } env
        ? env
        : Path.Combine(Home, ".codex");
    public static string SessionsDir => Path.Combine(CodexHome, "sessions");
    public static string WbProjects => Path.Combine(Home, ".workbuddy", "projects");
    public static string LogFile => Path.Combine(Path.GetTempPath(), "codex-pet.log");
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static PetConfig Load()
    {
        try
        {
            var text = File.ReadAllText(AppPaths.ConfigFile);
            return JsonSerializer.Deserialize<PetConfig>(text, Opts) ?? new PetConfig();
        }
        catch { return new PetConfig(); }
    }

    public static void Save(PetConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(cfg, Opts));
        }
        catch { }
    }
}

public static class Log
{
    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(AppPaths.LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
