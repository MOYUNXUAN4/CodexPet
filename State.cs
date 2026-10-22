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
    public int Total { get; set; }
    public bool Ready { get; set; }
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
    public string Mode { get; set; } = "codex";
    public bool Expanded { get; set; }
    public bool CurveOpen { get; set; }
}

public sealed class PetConfig
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Height { get; set; }
    public string Mode { get; set; } = "codex";
    public bool Expanded { get; set; }
    public bool CurveOpen { get; set; }
    // 记住上次可用的取数通道：http / curl
    public string NetTransport { get; set; }
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
