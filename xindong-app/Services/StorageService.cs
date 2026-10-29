using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XinDong.Models;

namespace XinDong.Services;

/// <summary>
/// 本地持久化服务：
/// <list type="bullet">
/// <item>配置与账本的 JSON 读写（原子写入，避免断电写坏文件）；</item>
/// <item>每日自动备份（保留最近 7 份）；</item>
/// <item>日 / 周 / 月 / 年聚合统计；</item>
/// <item>CSV 报表导出（UTF-8 BOM，Excel 双击不乱码）。</item>
/// </list>
/// 所有文件固定落在 exe 同级目录，保证绿色版整个文件夹可迁移。
/// </summary>
public sealed class StorageService
{
    /// <summary>日期格式常量。</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>JSON 序列化选项：缩进、中文不转义、枚举写字符串。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>exe 所在目录（绿色版数据根目录）。</summary>
    public string BaseDirectory { get; }

    /// <summary>配置文件完整路径。</summary>
    public string ConfigPath { get; }

    /// <summary>账本文件完整路径。</summary>
    public string LedgerPath { get; }

    /// <summary>最近一次错误信息（供界面提示）。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// 构造持久化服务。
    /// </summary>
    /// <param name="baseDirectory">数据目录，默认取 AppDomain.CurrentDomain.BaseDirectory。</param>
    public StorageService(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
            : baseDirectory!;
        ConfigPath = Path.Combine(BaseDirectory, "xindong_config.json");
        LedgerPath = Path.Combine(BaseDirectory, "xindong_ledger.json");
    }

    #region 配置读写

    /// <summary>
    /// 读取用户配置；文件不存在或损坏时返回一份默认配置，并完成字段规整。
    /// </summary>
    public UserConfig LoadConfig()
    {
        UserConfig config;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                config = JsonSerializer.Deserialize<UserConfig>(json, JsonOptions) ?? new UserConfig();
            }
            else
            {
                config = new UserConfig();
            }
        }
        catch (Exception ex)
        {
            LastError = "读取配置失败，已使用默认配置：" + ex.Message;
            config = new UserConfig();
        }

        Normalize(config);
        return config;
    }

    /// <summary>
    /// 保存用户配置（先写临时文件再替换，避免写入中断产生半截 JSON）。
    /// </summary>
    public bool SaveConfig(UserConfig config)
    {
        try
        {
            Normalize(config);
            WriteAllTextAtomic(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            LastError = "保存配置失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>字段规整：修补空集合、非法数值、过期判断，确保后续计算不抛异常。</summary>
    private static void Normalize(UserConfig config)
    {
        config.Holidays ??= new List<string>();
        config.MakeupDays ??= new List<string>();
        config.WorkDays ??= new List<DayOfWeek>();
        config.HolidayCalendar ??= new HolidayConfig();

        MigrateLegacyHolidayData(config);
        NormalizeHolidayCalendar(config.HolidayCalendar);

        if (config.WorkDays.Count == 0)
        {
            config.WorkDays.AddRange(new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday
            });
        }

        // 去重并按周一~周日排序，保证界面勾选状态稳定
        config.WorkDays = config.WorkDays.Distinct().OrderBy(d => (int)d).ToList();

        // 大小休没有锚点时，以本周为默认锚点，避免旧配置升级后无法判定
        if (config.RestSystem == RestSystem.BigSmallWeek)
        {
            config.HolidayCalendar.BigSmallWeekAnchorMonday ??= UserConfig.WeekMonday(DateTime.Today);
            config.HolidayCalendar.BigSmallWeekAnchorMonday =
                UserConfig.WeekMonday(config.HolidayCalendar.BigSmallWeekAnchorMonday.Value);
        }

        if (config.StandardHours <= 0.1) config.StandardHours = 8.0;
        if (config.MonthlyRent < 0m) config.MonthlyRent = 0m;
        if (config.DailyCommuteFood < 0m) config.DailyCommuteFood = 0m;
        if (config.PayDay is < 1 or > 31) config.PayDay = 15;
        if (config.MonthlyToleranceLimit < 0m) config.MonthlyToleranceLimit = 0m;

        if (config.NormalOvertimeStageHours <= 0) config.NormalOvertimeStageHours = 2.0;
        if (config.HolidayOvertimeStageHours <= 0) config.HolidayOvertimeStageHours = 8.0;
        if (config.OvertimeBaseSalary < 0m) config.OvertimeBaseSalary = 0m;

        // 旧版单字段基数迁移：>0 表示当年确实启用了自定义基数
        if (config.OvertimeBaseMonthly > 0m)
        {
            if (!config.UseCustomOvertimeBase && config.OvertimeBaseSalary <= 2360m)
            {
                config.UseCustomOvertimeBase = true;
                config.OvertimeBaseSalary = config.OvertimeBaseMonthly;
            }

            config.OvertimeBaseMonthly = 0m;
        }

        config.WorkStart = NormalizeTime(config.WorkStart, "09:00");
        config.WorkEnd = NormalizeTime(config.WorkEnd, "18:00");
        config.LunchStart = NormalizeTime(config.LunchStart, "12:00");
        config.LunchEnd = NormalizeTime(config.LunchEnd, "13:00");

        // H 不再手填：按上下班与午休自动折算（7.5 小时就是 08:30-17:30 扣 12:00-13:30）
        config.StandardHours = config.ComputedStandardHours;

        // 「今日休假」只在当天有效，跨天自动失效，防止第二天继续停计
        if (!string.Equals(config.TodayOffDate, DateTime.Today.ToString(DateFormat, CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            config.TodayOff = false;
        }
    }

    /// <summary>把旧版两个日期列表迁移到新的 HolidayConfig，迁移后清空旧字段。</summary>
    private static void MigrateLegacyHolidayData(UserConfig config)
    {
        var calendar = config.HolidayCalendar;

        foreach (var item in config.Holidays)
        {
            if (!TryNormalizeDate(item, out var date)) continue;
            calendar.Holidays[date] = "历史节假日";
        }

        foreach (var item in config.MakeupDays)
        {
            if (!TryNormalizeDate(item, out var date)) continue;
            calendar.Workdays.Add(date);
            calendar.Holidays.Remove(date);
        }

        // 旧字段已完成使命，清空避免每次保存重复迁移
        config.Holidays.Clear();
        config.MakeupDays.Clear();
    }

    /// <summary>规整日期集合：只保留合法 yyyy-MM-dd，并处理互斥优先级。</summary>
    private static void NormalizeHolidayCalendar(HolidayConfig calendar)
    {
        calendar.Holidays ??= new Dictionary<string, string>(StringComparer.Ordinal);
        calendar.Workdays ??= new HashSet<string>(StringComparer.Ordinal);
        calendar.RegularRestDays ??= new HashSet<string>(StringComparer.Ordinal);

        var holidays = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in calendar.Holidays)
        {
            if (!TryNormalizeDate(pair.Key, out var date)) continue;
            var name = string.IsNullOrWhiteSpace(pair.Value) ? "停计假日" : pair.Value.Trim();
            holidays[date] = name;
        }

        var workdays = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in calendar.Workdays)
        {
            if (TryNormalizeDate(item, out var date)) workdays.Add(date);
        }

        var regularRest = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in calendar.RegularRestDays)
        {
            if (TryNormalizeDate(item, out var date)) regularRest.Add(date);
        }

        // 补班优先级最高：同一天不再保留停计假日/常规休息
        foreach (var workday in workdays)
        {
            holidays.Remove(workday);
            regularRest.Remove(workday);
        }

        // 停计假日优先于常规休息
        foreach (var holiday in holidays.Keys)
        {
            regularRest.Remove(holiday);
        }

        calendar.Holidays = holidays;
        calendar.Workdays = workdays;
        calendar.RegularRestDays = regularRest;
    }

    /// <summary>把任意日期字符串规整成 yyyy-MM-dd。</summary>
    private static bool TryNormalizeDate(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!DateTime.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return false;
        }

        normalized = date.ToString(DateFormat, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>把任意时间字符串规整为 HH:mm；解析失败则回退默认值。</summary>
    private static string NormalizeTime(string? text, string fallback)
    {
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) &&
            ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
        {
            return $"{ts.Hours:00}:{ts.Minutes:00}";
        }

        return fallback;
    }

    #endregion

    #region 账本读写与备份

    /// <summary>读取历史账本；文件缺失或损坏时返回空账本。</summary>
    public Ledger LoadLedger()
    {
        var ledger = new Ledger();
        try
        {
            if (File.Exists(LedgerPath))
            {
                var json = File.ReadAllText(LedgerPath, Encoding.UTF8);
                ledger = JsonSerializer.Deserialize<Ledger>(json, JsonOptions) ?? new Ledger();
            }
        }
        catch (Exception ex)
        {
            LastError = "读取账本失败，已从备份恢复尝试：" + ex.Message;
            ledger = TryLoadLedgerBackup() ?? new Ledger();
        }

        ledger.Records ??= new List<DailyRecord>();

        // 同一天只保留最后一条（历史上万一产生重复记录，以最新为准）
        ledger.Records = ledger.Records
            .Where(r => !string.IsNullOrWhiteSpace(r.Date))
            .GroupBy(r => r.Date, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.LastUpdated).First())
            .OrderBy(r => r.Date, StringComparer.Ordinal)
            .ToList();

        foreach (var record in ledger.Records)
        {
            record.SlackingStats ??= new List<SlackingStat>();
        }

        return ledger;
    }

    /// <summary>保存账本（原子写入）。</summary>
    public bool SaveLedger(Ledger ledger)
    {
        try
        {
            ledger.Records = ledger.Records
                .OrderBy(r => r.Date, StringComparer.Ordinal)
                .ToList();
            WriteAllTextAtomic(LedgerPath, JsonSerializer.Serialize(ledger, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            LastError = "保存账本失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 每日首次启动时备份账本为 xindong_ledger.bak.yyyyMMdd.json，并只保留最近 7 份。
    /// 必须在第一次写账本之前调用。
    /// </summary>
    public void EnsureDailyBackup()
    {
        try
        {
            if (!File.Exists(LedgerPath)) return;

            var stamp = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(BaseDirectory, $"xindong_ledger.bak.{stamp}.json");
            if (!File.Exists(backupPath))
            {
                File.Copy(LedgerPath, backupPath, overwrite: false);
            }

            // 清理超过 7 份的旧备份，避免绿色文件夹越来越大
            var backups = Directory.GetFiles(BaseDirectory, "xindong_ledger.bak.*.json")
                .OrderByDescending(File.GetLastWriteTime)
                .Skip(7)
                .ToList();

            foreach (var old in backups)
            {
                try { File.Delete(old); } catch { /* 备份清理失败不影响主流程 */ }
            }
        }
        catch (Exception ex)
        {
            LastError = "自动备份失败（不影响使用）：" + ex.Message;
        }
    }

    /// <summary>按日期从新到旧查找可用备份并尝试加载。</summary>
    private Ledger? TryLoadLedgerBackup()
    {
        try
        {
            var backups = Directory.GetFiles(BaseDirectory, "xindong_ledger.bak.*.json")
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();

            foreach (var file in backups)
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var ledger = JsonSerializer.Deserialize<Ledger>(json, JsonOptions);
                    if (ledger is not null) return ledger;
                }
                catch
                {
                    // 坏备份继续尝试下一个
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    #endregion

    #region 聚合统计

    /// <summary>
    /// 按日 / 周 / 月 / 年聚合账本，返回看板所需的全部指标。
    /// 周范围固定为「周一 00:00 ~ 周日 23:59」，月 / 年为自然月 / 自然年。
    /// </summary>
    /// <param name="ledger">账本。</param>
    /// <param name="mode">统计维度。</param>
    /// <param name="anchor">锚点日期（选中日 / 周内任意一天 / 月内任意一天）。</param>
    /// <param name="config">用于判定工作日、生存线与熔断额度。</param>
    public AggregateResult BuildAggregate(Ledger ledger, ViewMode mode, DateTime anchor, UserConfig config)
    {
        var (start, end) = GetRange(mode, anchor);
        var startText = start.ToString(DateFormat, CultureInfo.InvariantCulture);
        var endText = end.ToString(DateFormat, CultureInfo.InvariantCulture);

        // 以字符串日期比较即可，账本日期统一为 yyyy-MM-dd，字典序等价于时间序
        var records = ledger.Records
            .Where(r => string.CompareOrdinal(r.Date, startText) >= 0 &&
                        string.CompareOrdinal(r.Date, endText) <= 0)
            .ToList();

        var activeDays = records.Count(r => r.WorkedSeconds + r.OvertimeSeconds > 0.5);
        var survivalLine = config.DailySurvivalLine;

        // 休息日按当前排班逐日判定（补班日会从休息日中扣除）
        var restDays = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (!config.IsWorkday(date)) restDays++;
        }

        // 已结清生存线：有实际出勤、且当日实得 >= 每日生存线
        var settledDays = records.Count(r =>
        {
            if (r.NormalEarned <= 0m) return false;
            if (!DateTime.TryParseExact(r.Date, DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) return false;
            if (!config.IsWorkday(date)) return false;
            return survivalLine <= 0m || r.NormalEarned >= survivalLine;
        });

        // 旧账本没有摸鱼次数 / 抹零秒数，做近似兜底
        var slackingCount = records.Sum(r =>
            r.SlackingCount > 0
                ? r.SlackingCount
                : r.SlackingSeconds > 0
                    ? Math.Max(1, r.SlackingStats?.Count ?? 0)
                    : 0);

        var roundingLostSeconds = records.Sum(r =>
        {
            if (r.RoundingLostSeconds > 0) return r.RoundingLostSeconds;
            if (r.RoundingLoss > 0m && config.SalaryMode == SalaryMode.Hourly && config.SecondSalary > 0m)
            {
                return (double)(r.RoundingLoss / config.SecondSalary);
            }

            return 0d;
        });

        var totalIncome = records.Sum(r => r.NormalEarned);

        var result = new AggregateResult
        {
            Mode = mode,
            Title = mode.BoardTitle(),
            Start = start,
            End = end,
            RangeText = $"{startText} ~ {endText}",
            DayCount = records.Count,
            ActiveDayCount = activeDays,
            TotalPaidSeconds = records.Sum(r => r.WorkedSeconds),
            TotalOvertimeSeconds = records.Sum(r => r.OvertimeSeconds),
            EmergencyCallSeconds = records.Sum(r => r.EmergencyCallSeconds),
            EmergencyCallLoss = records.Sum(r => r.EmergencyCallLoss),
            EmergencyCallEarned = records.Sum(r => r.EmergencyCallEarned),
            EmergencyCallCount = records.Sum(r => r.EmergencyCallCount),
            TotalSlackingSeconds = records.Sum(r => r.SlackingSeconds),
            TotalIncome = totalIncome,
            SlackingTotal = records.Sum(r => r.SlackingEarned),
            OvertimeTotal = records.Sum(r => r.OvertimeLoss),
            RoundingTotal = records.Sum(r => r.RoundingLoss),
            RestDayCount = restDays,
            SettledSurvivalDays = settledDays,
            ProfitNet = totalIncome - survivalLine * activeDays,
            SlackingCount = slackingCount,
            RoundingLostSeconds = roundingLostSeconds,
            DailySurvivalLine = survivalLine,
            ToleranceLimit = mode switch
            {
                ViewMode.Monthly => config.MonthlyToleranceLimit,
                ViewMode.Yearly => config.MonthlyToleranceLimit * 12m,
                _ => 0m
            }
        };

        // 摸鱼姿势成分分析：按金额聚合，占比 = 该姿势金额 / 摸鱼总额
        var grouped = records
            .SelectMany(r => r.SlackingStats ?? new List<SlackingStat>())
            .GroupBy(s => s.Type)
            .Select(g => new SlackingSlice
            {
                Type = g.Key,
                Seconds = g.Sum(x => x.Seconds),
                Amount = g.Sum(x => x.Amount)
            })
            .OrderByDescending(s => s.Amount)
            .ToList();

        foreach (var slice in grouped)
        {
            slice.Percent = result.SlackingTotal <= 0m
                ? 0
                : (double)(slice.Amount / result.SlackingTotal * 100m);
            result.Slices.Add(slice);
        }

        return result;
    }

    /// <summary>计算某维度的时间区间（含首尾两天）。</summary>
    public static (DateTime Start, DateTime End) GetRange(ViewMode mode, DateTime anchor)
    {
        var day = anchor.Date;
        switch (mode)
        {
            case ViewMode.Daily:
                return (day, day);

            case ViewMode.Weekly:
            {
                // DayOfWeek：周日 = 0，这里换算成「周一为一周之首」
                var offset = ((int)day.DayOfWeek + 6) % 7;
                var monday = day.AddDays(-offset);
                return (monday, monday.AddDays(6));
            }

            case ViewMode.Monthly:
                return (new DateTime(day.Year, day.Month, 1), new DateTime(day.Year, day.Month, 1).AddMonths(1).AddDays(-1));

            case ViewMode.Yearly:
                return (new DateTime(day.Year, 1, 1), new DateTime(day.Year, 12, 31));

            default:
                return (day, day);
        }
    }

    #endregion

    #region CSV 导出

    /// <summary>
    /// 导出账本为 CSV（UTF-8 BOM，Excel 直接双击不乱码）。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="records">要导出的记录（通常传全部历史）。</param>
    public void ExportCsv(string path, IEnumerable<DailyRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("日期,正常收益(元),摸鱼白嫖(元),义务加班亏损(元),抹零损失(元),有效工时(小时),总工时(小时),摸鱼时长(分钟),加班时长(分钟),是否节假日,净值(元),夺命Call亏损(元),夺命Call收益(元),夺命Call时长(分钟),夺命Call次数,是否自动补齐");

        foreach (var r in records.OrderBy(r => r.Date, StringComparer.Ordinal))
        {
            sb.Append(r.Date).Append(',')
              .Append(FormatNumber(r.NormalEarned, 4)).Append(',')
              .Append(FormatNumber(r.SlackingEarned, 4)).Append(',')
              .Append(FormatNumber(r.OvertimeLoss, 4)).Append(',')
              .Append(FormatNumber(r.RoundingLoss, 4)).Append(',')
              .Append((r.WorkedSeconds / 3600.0).ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(((r.WorkedSeconds + r.OvertimeSeconds) / 3600.0).ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append((r.SlackingSeconds / 60.0).ToString("F0", CultureInfo.InvariantCulture)).Append(',')
              .Append((r.OvertimeSeconds / 60.0).ToString("F0", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.IsHoliday ? "是" : "否").Append(',')
              .Append(FormatNumber(r.HedgeNet, 4)).Append(',')
              .Append(FormatNumber(r.EmergencyCallLoss, 4)).Append(',')
              .Append(FormatNumber(r.EmergencyCallEarned, 4)).Append(',')
              .Append((r.EmergencyCallSeconds / 60.0).ToString("F0", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.EmergencyCallCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.AutoFilled ? "是" : "否")
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>CSV 数字统一用不变文化格式化，避免系统区域设置把小数逗号变成分隔符。</summary>
    private static string FormatNumber(decimal value, int digits)
        => value.ToString("F" + digits, CultureInfo.InvariantCulture);

    #endregion

    #region 基础 IO

    /// <summary>
    /// 原子写文件：先写 .tmp，再替换目标文件。
    /// 任何一个时刻磁盘上都至少有一份完整 JSON。
    /// </summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    #endregion
}
