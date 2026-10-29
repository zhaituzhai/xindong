using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XinDong.Models;

namespace XinDong.Services;

/// <summary>注册结果状态机。</summary>
public enum RegistrationState
{
    /// <summary>无设备身份（理论上不会出现，LoadOrCreate 已保证）。</summary>
    NoDevice,
    /// <summary>未注册（离线，待下次重试）。</summary>
    Unregistered,
    /// <summary>已注册，token 有效。</summary>
    Registered,
    /// <summary>后端不可达，保留本地状态继续单机运行。</summary>
    Offline
}

/// <summary>
/// 后端 HTTP 客户端：设备注册、数据上报、榜单拉取、花名绑定。
/// 所有网络错误静默返回失败，不抛异常，不阻断本地功能。
/// </summary>
public sealed class ApiService : IDisposable
{
    private const string ClientVersion = "1.0.0";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http = new() { Timeout = Timeout };
    private readonly DeviceIdService _device;

    public ApiService(DeviceIdService device) => _device = device;

    // ---------- 注册 ----------

    /// <summary>
    /// 确保设备已注册并持有有效 token。有 token 先探活；无 token 或失效则发 /report 注册。
    /// </summary>
    public async Task<RegistrationState> EnsureRegisteredAsync(
        DeviceIdentity identity, UserConfig config, Ledger ledger, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(identity.Token))
        {
            var probe = await ProbeTokenAsync(identity, ct);
            if (probe == ProbeResult.Ok) return RegistrationState.Registered;
            if (probe == ProbeResult.InvalidToken)
            {
                identity.Token = null;
                _device.Save(identity);
            }
            if (probe == ProbeResult.NetworkError) return RegistrationState.Offline;
        }

        var payload = BuildReportPayload(identity, config, ledger);
        var resp = await PostReportAsync(identity.ServerBaseUrl, payload, token: null, ct);
        if (resp is null) return RegistrationState.Offline;

        identity.Token = resp.Token;
        identity.Nickname = resp.Nickname;
        identity.RegisteredAt = DateTime.Now.ToString("o");
        _device.Save(identity);
        return RegistrationState.Registered;
    }

    private enum ProbeResult { Ok, InvalidToken, NetworkError }

    private async Task<ProbeResult> ProbeTokenAsync(DeviceIdentity identity, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{identity.ServerBaseUrl}/api/v1/leaderboard/me?scope=daily");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            using var resp = await _http.SendAsync(req, ct);
            return resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? ProbeResult.InvalidToken
                : ProbeResult.Ok;
        }
        catch
        {
            return ProbeResult.NetworkError;
        }
    }

    // ---------- 上报 ----------

    /// <summary>全量上报 config + ledger（需已注册）。成功返回 true。</summary>
    public async Task<bool> ReportAsync(
        DeviceIdentity identity, UserConfig config, Ledger ledger, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identity.Token)) return false;
        var payload = BuildReportPayload(identity, config, ledger);
        var resp = await PostReportAsync(identity.ServerBaseUrl, payload, identity.Token, ct);
        return resp is not null;
    }

    /// <summary>心跳：仅更新在线状态，payload 同 report 但 event_type=heartbeat。</summary>
    public async Task<bool> HeartbeatAsync(
        DeviceIdentity identity, UserConfig config, Ledger ledger, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identity.Token)) return false;
        var payload = BuildReportPayload(identity, config, ledger);
        payload.EventType = "heartbeat";
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{identity.ServerBaseUrl}/api/v1/heartbeat")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ReportResponse?> PostReportAsync(
        string baseUrl, ReportPayload payload, string? token, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/report")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<ReportResponse>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 花名 ----------

    /// <summary>绑定花名。返回 (ok, conflict)；conflict=true 表示花名被占用。</summary>
    public async Task<(bool ok, bool conflict)> BindNicknameAsync(
        DeviceIdentity identity, string nickname, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identity.Token)) return (false, false);
        try
        {
            var body = JsonSerializer.Serialize(new { nickname }, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{identity.ServerBaseUrl}/api/v1/bind/nickname")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict) return (false, true);
            if (!resp.IsSuccessStatusCode) return (false, false);
            identity.Nickname = nickname;
            _device.Save(identity);
            return (true, false);
        }
        catch
        {
            return (false, false);
        }
    }

    // ---------- 榜单 ----------

    /// <summary>拉取榜单。scope: daily/weekly；metric 见 rank.METRICS。</summary>
    public async Task<LeaderboardResponse?> GetLeaderboardAsync(
        DeviceIdentity identity, string scope, string metric, string? date, int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            var q = $"?metric={Uri.EscapeDataString(metric)}&limit={limit}";
            if (!string.IsNullOrEmpty(date)) q += $"&date={Uri.EscapeDataString(date)}";
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{identity.ServerBaseUrl}/api/v1/leaderboard/{scope}{q}");
            if (!string.IsNullOrEmpty(identity.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<LeaderboardResponse>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>拉取我在所有指标的位次。返回 metric → position。</summary>
    public async Task<Dictionary<string, MyMetricPosition>?> GetMyPositionAsync(
        DeviceIdentity identity, string scope, string? date, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identity.Token)) return null;
        try
        {
            var q = $"?scope={Uri.EscapeDataString(scope)}";
            if (!string.IsNullOrEmpty(date)) q += $"&date={Uri.EscapeDataString(date)}";
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{identity.ServerBaseUrl}/api/v1/leaderboard/me{q}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<Dictionary<string, MyMetricPosition>>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 工具 ----------

    private static ReportPayload BuildReportPayload(DeviceIdentity identity, UserConfig config, Ledger ledger)
        => new()
        {
            DeviceId = identity.DeviceId,
            ClientVersion = ClientVersion,
            ClientTzOffset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
            ReportedAt = DateTime.Now.ToString("o"),
            Config = config,
            Ledger = ledger,
            EventType = "report"
        };

    public void Dispose() => _http.Dispose();
}