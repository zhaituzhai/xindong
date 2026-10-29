using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace XinDong.Services;

/// <summary>
/// 节假日同步服务：优先请求 timor.tech 公共接口，失败时读取编进 exe 的 2025 / 2026 兜底表。
/// 不依赖任何 NuGet 包，只使用 BCL 的 HttpClient + System.Text.Json。
/// </summary>
public sealed class HolidayService
{
    private const string ApiTemplate = "https://timor.tech/api/holiday/year/{0}";
    private const string ResourceName = "XinDong.Data.china_holidays.json";

    private static readonly HttpClient Http = CreateClient();
    private static Dictionary<int, HolidayYearData>? _fallbackCache;

    /// <summary>同步某一年，网络失败自动回退到内置表；两者都失败时返回失败结果。</summary>
    public async Task<HolidaySyncResult> SyncYearAsync(int year, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiTemplate, year));
            request.Headers.UserAgent.ParseAdd("XinDong/1.0");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseApi(json, year);
            if (parsed is not null)
            {
                parsed.Source = "网络接口";
                parsed.StatutoryDays = StatutoryDays(year);
                return parsed;
            }
        }
        catch
        {
            // 网络失败、超时、接口格式变化都统一走内置表
        }

        var fallback = LoadFallback(year);
        if (fallback is not null)
        {
            fallback.Source = "内置节假日表";
            return fallback;
        }

        return HolidaySyncResult.Fail($"同步失败：{year} 年既没有网络数据，也没有内置兜底表。");
    }

    /// <summary>读取年份的内置数据（用于启动/离线检查，不联网）。</summary>
    public HolidaySyncResult? LoadFallback(int year)
    {
        EnsureFallbackLoaded();
        if (_fallbackCache is null || !_fallbackCache.TryGetValue(year, out var data)) return null;

        return new HolidaySyncResult
        {
            Success = true,
            FromNetwork = false,
            Source = "内置节假日表",
            Year = year,
            Holidays = new Dictionary<string, string>(data.Holidays, StringComparer.Ordinal),
            Workdays = new HashSet<string>(data.Workdays, StringComparer.Ordinal),
            StatutoryDays = StatutoryDays(year)
        };
    }

    /// <summary>解析 timor.tech 的 year 接口；格式不符返回 null。</summary>
    private static HolidaySyncResult? ParseApi(string json, int year)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0) return null;
        if (!root.TryGetProperty("holiday", out var holidayMap) || holidayMap.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new HolidaySyncResult { Success = true, FromNetwork = true, Year = year };
        var prefix = year + "-";

        foreach (var item in holidayMap.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object) continue;

            var date = item.Value.TryGetProperty("date", out var dateElement)
                ? dateElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(date))
            {
                // 接口 key 是 MM-dd，补全年份
                date = year + "-" + item.Name;
            }

            if (string.IsNullOrWhiteSpace(date) || !date.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var isHoliday = item.Value.TryGetProperty("holiday", out var holidayElement) &&
                            holidayElement.ValueKind == JsonValueKind.True;
            var name = item.Value.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (isHoliday)
            {
                result.Holidays[date] = string.IsNullOrWhiteSpace(name) ? "节假日" : name!.Trim();
            }
            else
            {
                result.Workdays.Add(date);
            }
        }

        return result.Holidays.Count > 0 || result.Workdays.Count > 0 ? result : null;
    }

    /// <summary>加载嵌入资源中的 JSON 兜底表。</summary>
    private static void EnsureFallbackLoaded()
    {
        if (_fallbackCache is not null) return;

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                _fallbackCache = new Dictionary<int, HolidayYearData>();
                return;
            }

            using var doc = JsonDocument.Parse(stream);
            var map = new Dictionary<int, HolidayYearData>();

            foreach (var yearProperty in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(yearProperty.Name, out var year)) continue;
                if (yearProperty.Value.ValueKind != JsonValueKind.Object) continue;

                var data = new HolidayYearData();

                if (yearProperty.Value.TryGetProperty("holidays", out var holidays) &&
                    holidays.ValueKind == JsonValueKind.Object)
                {
                    foreach (var holiday in holidays.EnumerateObject())
                    {
                        data.Holidays[holiday.Name] = holiday.Value.GetString() ?? "节假日";
                    }
                }

                if (yearProperty.Value.TryGetProperty("workdays", out var workdays) &&
                    workdays.ValueKind == JsonValueKind.Array)
                {
                    foreach (var workday in workdays.EnumerateArray())
                    {
                        var text = workday.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) data.Workdays.Add(text);
                    }
                }

                map[year] = data;
            }

            _fallbackCache = map;
        }
        catch
        {
            _fallbackCache = new Dictionary<int, HolidayYearData>();
        }
    }

    /// <summary>全年法定节假日天数（用于提示文案，不用于日历计数）。</summary>
    private static int StatutoryDays(int year) => year switch
    {
        2025 => 13,
        2026 => 13,
        _ => 0
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XinDong/1.0");
        return client;
    }

    /// <summary>内置表的一行年份数据。</summary>
    private sealed class HolidayYearData
    {
        public Dictionary<string, string> Holidays { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Workdays { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>某个年份的节假日同步结果。</summary>
public sealed class HolidaySyncResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>是否来自网络。</summary>
    public bool FromNetwork { get; set; }

    /// <summary>数据来源说明。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>年份。</summary>
    public int Year { get; set; }

    /// <summary>停计假日（yyyy-MM-dd → 名称）。</summary>
    public Dictionary<string, string> Holidays { get; set; } = new(StringComparer.Ordinal);

    /// <summary>调休补班日。</summary>
    public HashSet<string> Workdays { get; set; } = new(StringComparer.Ordinal);

    /// <summary>全年法定节假日天数（仅用于提示）。</summary>
    public int StatutoryDays { get; set; }

    /// <summary>失败结果。</summary>
    public static HolidaySyncResult Fail(string message) => new()
    {
        Success = false,
        Source = "同步失败",
        Message = message
    };

    /// <summary>失败时的提示。</summary>
    public string Message { get; set; } = string.Empty;
}
