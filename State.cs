using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CodexPet;

public sealed class QuotaWindow
{
    public double Remaining { get; set; }
    public double Used { get; set; }
    public long WindowSeconds { get; set; }
    public long? ResetAt { get; set; }
}

public sealed class UsageInfo
{
    public string Plan { get; set; } = "--";
    public QuotaWindow FiveHour { get; set; }
    public QuotaWindow Weekly { get; set; }
    public bool LimitReached { get; set; }
}

public sealed class TeaseInfo
{
    public string Quote { get; set; }
    public string At { get; set; }
}

public sealed class ForecastInfo
{
    public int? P24 { get; set; }
    public int? P48 { get; set; }
    public double? Raw24 { get; set; }
    public double? Raw48 { get; set; }
    public string LastResetAt { get; set; }
    public double? AgeDays { get; set; }
    public double? Cadence { get; set; }
    public string Confidence { get; set; }
    public string UpdatedAt { get; set; }
    public TeaseInfo Tease { get; set; }
    public string Hint { get; set; }
}

public sealed class SessionInfo
{
    public string Title { get; set; }
    public string Cwd { get; set; }
    // 当前会话的 rollout 文件，右键菜单里「复制会话路径」用
    public string FilePath { get; set; }
    public bool Running { get; set; }
    public int? CtxPercent { get; set; }
    public long? CtxUsed { get; set; }
    public long? CtxWindow { get; set; }
    public string Model { get; set; }
    public string Effort { get; set; }
    public string Subagent { get; set; }
    public string SubagentKind { get; set; }
    // 主会话当前派发的子代理（显示它自己用的模型）
    public string SubModel { get; set; }
    public string SubEffort { get; set; }
    public string SubNick { get; set; }
    public bool SubRunning { get; set; }
    public long? SubLastAt { get; set; }
    public long LastActiveAt { get; set; }
    public string LastType { get; set; }
    // 这次对话里实际调用过的 skill（新的在前，最多 4 个）
    public List<string> Skills { get; set; } = new();
}

public sealed class AgModel
{
    public string Label { get; set; }
    public int Remaining { get; set; }
    public string ResetAt { get; set; }
}

public sealed class CreditPair
{
    public long Left { get; set; }
    public long Total { get; set; }
}

public sealed class AgCredits
{
    public CreditPair Prompt { get; set; } = new();
    public CreditPair Flow { get; set; } = new();
}

public sealed class AgQuota
{
    public int Remaining { get; set; }
    public string ResetAt { get; set; }
}

public sealed class AgGroup
{
    public string Name { get; set; }
    public string Desc { get; set; }
    public AgQuota Weekly { get; set; }
    public AgQuota FiveHour { get; set; }
}

public sealed class AgInfo
{
    public string Plan { get; set; }
    public string Tier { get; set; }
    public List<AgModel> Models { get; set; } = new();
    public List<AgGroup> Groups { get; set; } = new();
    public AgCredits Credits { get; set; } = new();
}

public sealed class WbInfo
{
    public string Model { get; set; }
    public string ModelLabel { get; set; }
    public string Agent { get; set; }
    // 最近活动的会话文件（完成条「打开会话」用）
    public string FilePath { get; set; }
    public bool Running { get; set; }
    public long? ModelAt { get; set; }
    public double TodayCredit { get; set; }
    public double WeekCredit { get; set; }
    public int TodayReq { get; set; }
    public int WeekReq { get; set; }
    public int Sessions { get; set; }
    public long? LastActiveAt { get; set; }
    public List<double> Hourly { get; set; } = new();
    public List<double> Daily { get; set; } = new();
    public List<string> Skills { get; set; } = new();
    public bool Ready { get; set; }
}

// 某个 agent 完成一次任务的记录（用于角标 / 提示条 / 卡片闪一下）
public sealed class DoneEvent
{
    public string Agent { get; set; }        // codex / antigravity / workbuddy
    public int Seq { get; set; }
    public long At { get; set; }
    public string Label { get; set; }        // 完成时用的模型，如 "6.1 sol 中"
    public long ElapsedMs { get; set; }      // 这一轮跑了多久（未知时为 0）
}

public sealed class HistoryPayload
{
    public List<double[]> H5 { get; set; } = new();
    public List<double[]> Wk { get; set; } = new();
    // 本月（每月 5 号起）的周额度采样，前端据此累加出月度消耗曲线
    public List<double[]> Mo { get; set; } = new();
    public int Total { get; set; }
    public bool Ready { get; set; }
}

// 30 天 × 24 小时的活跃热力图（按本地小时聚合生成量，单位 k token）
public sealed class HeatPayload
{
    public List<List<int>> Grid { get; set; } = new();   // [天][小时]
    public List<string> Days { get; set; } = new();      // 每天对应的 "MM-DD"
    public int Max { get; set; }
    public int Total { get; set; }                       // 30 天总量（k）
    public int PeakDay { get; set; }
    public int PeakHour { get; set; }
    // 本月（5 号起）累计 token 曲线：[时间戳, 累计 k token]
    public List<double[]> Month { get; set; } = new();
    public int MonthTotal { get; set; }
    public long MonthStart { get; set; }
    public string PeakText { get; set; }
    public long UpdatedAt { get; set; }
    public bool Ready { get; set; }
}

public sealed class HeatCacheEntry
{
    public long Size { get; set; }
    public double Mtime { get; set; }
    public Dictionary<string, double> Hours { get; set; } = new();   // 本地小时起点(ms) -> 生成 token
}

public sealed class HeatCacheFile
{
    public Dictionary<string, HeatCacheEntry> Files { get; set; } = new();
}

public sealed class PetState
{
    public UsageInfo Usage { get; set; }
    public ForecastInfo Forecast { get; set; }
    public SessionInfo Session { get; set; }
    public AgInfo Antigravity { get; set; }
    public string AgError { get; set; }
    public string UsageError { get; set; }
    public string ForecastError { get; set; }
    public long? LastUsageAt { get; set; }
    public long? LastForecastAt { get; set; }
    public long? LastAgAt { get; set; }
    public HistoryPayload History { get; set; } = new() { Ready = false };
    public WbInfo Wb { get; set; }
    // 最近完成的任务（新的在前），Seq 单调递增，前端据此判断有没有新事件
    public List<DoneEvent> Done { get; set; } = new();
    public int DoneSeq { get; set; }
    public HeatPayload Heat { get; set; } = new() { Ready = false };
    public string Mode { get; set; } = "codex";
    public bool Expanded { get; set; }
    public bool CurveOpen { get; set; }
    // 曲线里的二级折叠：本月累计
    public bool MonthOpen { get; set; }
    // 曲线里的二级折叠：活跃热力图
    public bool HeatOpen { get; set; }
    // 桌宠小人是否折起来（用「折叠」语义做默认值，老配置没这个字段时就是展开的）
    public bool PetClosed { get; set; }
    // 整张卡片折叠成一条（只留头部）
    public bool Collapsed { get; set; }
    // 三个 agent 最近活动的会话文件：点完成条直接定位到那个 rollout / transcript
    public string WbFile { get; set; }
    public string AgFile { get; set; }
    // 成本估算：每百万 output token 的美元单价，以及折算汇率
    public double CostPerMTok { get; set; } = 2.0;
    public double FxRate { get; set; } = 7.1;
}

public sealed class PetConfig
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Height { get; set; }
    public string Mode { get; set; } = "codex";
    public bool Expanded { get; set; }
    public bool CurveOpen { get; set; }
    public bool MonthOpen { get; set; }
    public bool HeatOpen { get; set; }
    public bool PetClosed { get; set; }
    public bool Collapsed { get; set; }
    // 记住上次可用的取数通道：http / curl
    public string NetTransport { get; set; }
    // 成本估算（可手改 codex-pet-config.json）：每百万 token 美元价 + 汇率
    public double? PricePerMTok { get; set; }
    public double? FxRate { get; set; }
}

public sealed class HistSample
{
    public long T { get; set; }
    public double? P5 { get; set; }
    public double? Pw { get; set; }
}

public sealed class ScanStamp
{
    public long Size { get; set; }
    public double Mtime { get; set; }
}

public sealed class HistoryFile
{
    public List<HistSample> Samples { get; set; } = new();
    public Dictionary<string, ScanStamp> Scanned { get; set; } = new();
}
