using System.Text.Json.Serialization;

namespace XinDong.Models;

/// <summary>设备身份与登录凭证，持久化到 xindong_device.json。</summary>
public sealed class DeviceIdentity
{
    /// <summary>设备指纹（32 字符 hex 或 GUID N 格式），主键。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>后端颁发的 token；注册前为 null。</summary>
    public string? Token { get; set; }

    /// <summary>已在后端绑定的花名镜像；未绑定为 null。</summary>
    public string? Nickname { get; set; }

    /// <summary>注册成功时间（ISO 8601）；注册前为 null。</summary>
    public string? RegisteredAt { get; set; }

    /// <summary>后端服务地址。</summary>
    public string ServerBaseUrl { get; set; } = "http://localhost:8770";

    /// <summary>指纹来源（"MachineGuid+Cpu" / "Guid"），用于诊断。</summary>
    public string FingerprintSource { get; set; } = string.Empty;

    /// <summary>本机首次生成时间（ISO 8601）。</summary>
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>POST /api/v1/report 请求体。字段名按后端 pydantic 模型用 snake_case。</summary>
public sealed class ReportPayload
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("client_version")] public string? ClientVersion { get; set; }
    [JsonPropertyName("client_tz_offset")] public int? ClientTzOffset { get; set; }
    [JsonPropertyName("reported_at")] public string ReportedAt { get; set; } = string.Empty;
    /// <summary>用户配置（UserConfig 整对象，PascalCase 序列化）。</summary>
    [JsonPropertyName("config")] public UserConfig? Config { get; set; }
    /// <summary>账本（Ledger 整对象，PascalCase 序列化）。</summary>
    [JsonPropertyName("ledger")] public Ledger? Ledger { get; set; }
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "report";
}

/// <summary>/report 与 /heartbeat 的响应体。</summary>
public sealed class ReportResponse
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
}

/// <summary>榜单单行。</summary>
public sealed class LeaderboardEntry
{
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("is_me")] public bool IsMe { get; set; }
    [JsonPropertyName("diff_to_top")] public double? DiffToTop { get; set; }
    [JsonPropertyName("diff_to_prev")] public double? DiffToPrev { get; set; }
    [JsonPropertyName("absolute")] public double? Absolute { get; set; }
    [JsonPropertyName("is_money")] public bool IsMoney { get; set; }
}

/// <summary>榜单响应。</summary>
public sealed class LeaderboardResponse
{
    [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("metric")] public string Metric { get; set; } = string.Empty;
    [JsonPropertyName("metric_label")] public string MetricLabel { get; set; } = string.Empty;
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("entries")] public List<LeaderboardEntry> Entries { get; set; } = new();
}

/// <summary>我的排名单指标结果（/leaderboard/me 返回 metric → 该结构）。</summary>
public sealed class MyMetricPosition
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("absolute")] public double? Absolute { get; set; }
    [JsonPropertyName("percentile")] public double? Percentile { get; set; }
}