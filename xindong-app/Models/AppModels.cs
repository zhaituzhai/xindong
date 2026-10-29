using System.Globalization;
using System.Text.Json.Serialization;

namespace XinDong.Models;

#region 枚举定义

/// <summary>
/// 工作状态机：描述当前这一秒薪资引擎所处的阶段。
/// 状态由「时间 + 排班 + 节假日 + 用户操作」共同推导，是整套计算逻辑的唯一入口。
/// </summary>
public enum WorkState
{
    /// <summary>未到上班时间（或今日尚未开始）。</summary>
    NotStarted,

    /// <summary>正常搬砖计薪。</summary>
    Working,

    /// <summary>带薪摸鱼中（总工资照常累加，同时单独累计白嫖金额）。</summary>
    Slacking,

    /// <summary>午休冻结（暂停计薪）。</summary>
    LunchBreak,

    /// <summary>已确认下班，停止计薪。</summary>
    OffDuty,

    /// <summary>节假日 / 休息日停止计费。</summary>
    Holiday,

    /// <summary>义务加班（老板白嫖，工资停跳、亏损狂跳）。</summary>
    Overtime,

    /// <summary>老板夺命 Call：下班 / 休假后被临时抓回，工资锁死或按报酬模式实时计入。</summary>
    EmergencyOvertime
}

/// <summary>夺命 Call 的报酬方式。</summary>
public enum EmergencyCallCompensationMode
{
    /// <summary>纯义务白嫖：零工资，按 2.0× / 3.0× 累计老板少给金额。</summary>
    Unpaid,

    /// <summary>老板良心发现：结束时一次性给固定补贴。</summary>
    FixedSubsidy,

    /// <summary>老板良心发现：按用户填写的实际加班时薪实时计酬。</summary>
    HourlyRate
}

/// <summary>夺命 Call 启动参数（由弹窗选择后传回 ViewModel）。</summary>
public sealed class EmergencyCallOptions
{
    /// <summary>报酬方式。</summary>
    public EmergencyCallCompensationMode Mode { get; set; } = EmergencyCallCompensationMode.Unpaid;

    /// <summary>固定单次补贴（元），仅 <see cref="EmergencyCallCompensationMode.FixedSubsidy"/> 使用。</summary>
    public decimal FixedSubsidy { get; set; }

    /// <summary>实际支付的加班时薪（元/小时），仅 <see cref="EmergencyCallCompensationMode.HourlyRate"/> 使用。</summary>
    public decimal HourlyRate { get; set; }

    /// <summary>是否为有偿模式。</summary>
    [JsonIgnore]
    public bool IsPaid => Mode != EmergencyCallCompensationMode.Unpaid;
}

/// <summary>计薪模式。</summary>
public enum SalaryMode
{
    /// <summary>月薪模式：日薪 = 月薪 / D，秒薪 = 日薪 / (H × 3600)。</summary>
    Monthly,

    /// <summary>时薪模式：按半点 / 整点阶梯锁定，不满半点不计薪。</summary>
    Hourly
}

/// <summary>休息制度：决定折算月有效计薪天数 D。</summary>
public enum RestSystem
{
    /// <summary>无休：D = 30。</summary>
    NoRest,

    /// <summary>月休 2 天：D = 28。</summary>
    Rest2Days,

    /// <summary>月休 4 天：D = 26。</summary>
    Rest4Days,

    /// <summary>大小休（单双休轮换）：D = 24。</summary>
    BigSmallWeek,

    /// <summary>双休：D = 21.75。</summary>
    DoubleWeekend
}

/// <summary>摸鱼姿势。</summary>
public enum SlackingType
{
    /// <summary>🚽 带薪拉屎。</summary>
    Toilet,

    /// <summary>📱 刷手机 / 水群。</summary>
    Phone,

    /// <summary>☕ 泡茶拿外卖。</summary>
    Tea,

    /// <summary>😶 发呆放空。</summary>
    Daze
}

/// <summary>老板键伪装皮肤。</summary>
public enum BossKeyMode
{
    /// <summary>模式 A：彻底隐身，主窗口与灵动岛全部隐藏，仅保留托盘图标。</summary>
    Stealth,

    /// <summary>模式 B-1：伪装成 PLC / 上位机调试监控条。</summary>
    PlcCamouflage,

    /// <summary>模式 B-2：伪装成 Excel 状态栏。</summary>
    ExcelCamouflage
}

/// <summary>复盘看板时间维度。</summary>
public enum ViewMode
{
    /// <summary>日。</summary>
    Daily,

    /// <summary>周（周一至周日）。</summary>
    Weekly,

    /// <summary>月（自然月）。</summary>
    Monthly,

    /// <summary>年（自然年）。</summary>
    Yearly
}

#endregion

#region 枚举文本映射

/// <summary>
/// 枚举到中文文案 / 数值的映射扩展。
/// 统一收口，避免 UI 各处散落 switch 造成文案不一致。
/// </summary>
public static class EnumText
{
    /// <summary>休息制度对应的折算月计薪天数 D。</summary>
    public static decimal PayDays(this RestSystem rest) => rest switch
    {
        RestSystem.NoRest => 30m,
        RestSystem.Rest2Days => 28m,
        RestSystem.Rest4Days => 26m,
        RestSystem.BigSmallWeek => 24m,
        RestSystem.DoubleWeekend => 21.75m,
        _ => 21.75m
    };

    /// <summary>休息制度中文名。</summary>
    public static string DisplayName(this RestSystem rest) => rest switch
    {
        RestSystem.NoRest => "无休（30 天）",
        RestSystem.Rest2Days => "月休 2 天（28 天）",
        RestSystem.Rest4Days => "月休 4 天（26 天）",
        RestSystem.BigSmallWeek => "大小休（24 天）",
        RestSystem.DoubleWeekend => "双休（21.75 天）",
        _ => "双休（21.75 天）"
    };

    /// <summary>计薪模式中文名。</summary>
    public static string DisplayName(this SalaryMode mode) => mode switch
    {
        SalaryMode.Monthly => "月薪模式",
        SalaryMode.Hourly => "时薪模式",
        _ => "月薪模式"
    };

    /// <summary>摸鱼姿势中文名（带 emoji）。</summary>
    public static string DisplayName(this SlackingType type) => type switch
    {
        SlackingType.Toilet => "🚽 带薪拉屎",
        SlackingType.Phone => "📱 刷手机/水群",
        SlackingType.Tea => "☕ 泡茶拿外卖",
        SlackingType.Daze => "😶 发呆放空",
        _ => "🐟 摸鱼"
    };

    /// <summary>摸鱼姿势短名称（不带 emoji，用于图表标签）。</summary>
    public static string ShortName(this SlackingType type) => type switch
    {
        SlackingType.Toilet => "带薪拉屎",
        SlackingType.Phone => "刷手机/水群",
        SlackingType.Tea => "泡茶拿外卖",
        SlackingType.Daze => "发呆放空",
        _ => "摸鱼"
    };

    /// <summary>摸鱼姿势 emoji。</summary>
    public static string Emoji(this SlackingType type) => type switch
    {
        SlackingType.Toilet => "🚽",
        SlackingType.Phone => "📱",
        SlackingType.Tea => "☕",
        SlackingType.Daze => "😶",
        _ => "🐟"
    };

    /// <summary>老板键模式中文名。</summary>
    public static string DisplayName(this BossKeyMode mode) => mode switch
    {
        BossKeyMode.Stealth => "A · 彻底隐身（托盘灰图标）",
        BossKeyMode.PlcCamouflage => "B1 · PLC/上位机调试监控",
        BossKeyMode.ExcelCamouflage => "B2 · Excel 状态栏",
        _ => "A · 彻底隐身"
    };

    /// <summary>看板维度中文名。</summary>
    public static string DisplayName(this ViewMode mode) => mode switch
    {
        ViewMode.Daily => "📅 日",
        ViewMode.Weekly => "📊 周",
        ViewMode.Monthly => "📈 月",
        ViewMode.Yearly => "🗓️ 年",
        _ => "📅 日"
    };

    /// <summary>看板维度标题。</summary>
    public static string BoardTitle(this ViewMode mode) => mode switch
    {
        ViewMode.Daily => "今日收益总览",
        ViewMode.Weekly => "本周牛马战报",
        ViewMode.Monthly => "本月财务复盘",
        ViewMode.Yearly => "年度资本家剥削与反薅总账",
        _ => "今日收益总览"
    };
}

#endregion

#region 用户配置

/// <summary>
/// 节假日 / 调休 / 常规休息的日期规则。
/// 三套日期集合互斥优先级：Workdays（补班） &gt; Holidays（停计假日） &gt; RegularRestDays（常规休息）。
/// </summary>
public sealed class HolidayConfig
{
    /// <summary>法定或自定义的停计休假日，Key 为 yyyy-MM-dd，Value 为节日名称。</summary>
    public Dictionary<string, string> Holidays { get; set; } = new(StringComparer.Ordinal);

    /// <summary>调休补班 / 强制上班日，Key 为 yyyy-MM-dd。</summary>
    public HashSet<string> Workdays { get; set; } = new(StringComparer.Ordinal);

    /// <summary>手选或规则生成的常规休息日（灰色休），Key 为 yyyy-MM-dd。</summary>
    public HashSet<string> RegularRestDays { get; set; } = new(StringComparer.Ordinal);

    /// <summary>大小休锚点：某一周周一的日期；以此周为大周/小周向前后交替。</summary>
    public DateTime? BigSmallWeekAnchorMonday { get; set; }

    /// <summary>锚点周是否为大周（大周=周六日休 2 天，小周=仅周日休 1 天）。</summary>
    public bool BigSmallWeekAnchorIsBig { get; set; } = true;
}

/// <summary>
/// 用户配置模型：薪资、排班、节假日、生存成本、老板键等全部可持久化设置。
/// 该对象以 xindong_config.json 形式存放在 exe 同级目录，整个文件夹可随时拷走。
/// </summary>
public sealed class UserConfig
{
    /// <summary>配置结构版本号，便于将来做兼容迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>计薪模式：月薪 / 时薪。</summary>
    public SalaryMode SalaryMode { get; set; } = SalaryMode.Monthly;

    /// <summary>月薪金额（元）或时薪金额（元/小时），含义由 <see cref="SalaryMode"/> 决定。</summary>
    public decimal SalaryAmount { get; set; }

    /// <summary>休息制度（决定月有效计薪天数 D）。</summary>
    public RestSystem RestSystem { get; set; } = RestSystem.DoubleWeekend;

    /// <summary>上班时间，格式 HH:mm。</summary>
    public string WorkStart { get; set; } = "09:00";

    /// <summary>下班时间，格式 HH:mm。</summary>
    public string WorkEnd { get; set; } = "18:00";

    /// <summary>午休开始时间，格式 HH:mm。</summary>
    public string LunchStart { get; set; } = "12:00";

    /// <summary>午休结束时间，格式 HH:mm。</summary>
    public string LunchEnd { get; set; } = "13:00";

    /// <summary>每日标准有效工时 H（小时）。仅作旧数据兼容，实际以排班自动折算值为准。</summary>
    public double StandardHours { get; set; } = 8.0;

    /// <summary>每周工作日（默认周一至周五）。调休补班日会强制覆盖为工作日。</summary>
    public List<DayOfWeek> WorkDays { get; set; } = new()
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    };

    /// <summary>自定义节假日列表，格式 yyyy-MM-dd。旧字段，仅用于迁移，迁移后由 HolidayCalendar 接管。</summary>
    public List<string> Holidays { get; set; } = new();

    /// <summary>调休补班日列表。旧字段，仅用于迁移，迁移后由 HolidayCalendar 接管。</summary>
    public List<string> MakeupDays { get; set; } = new();

    /// <summary>节假日 / 调休 / 常规休息的日期规则。</summary>
    public HolidayConfig HolidayCalendar { get; set; } = new();

    /// <summary>今日休假开关（停止计费）。</summary>
    public bool TodayOff { get; set; }

    /// <summary>「今日休假」针对的具体日期，跨天自动失效，避免第二天误停计。</summary>
    public string TodayOffDate { get; set; } = string.Empty;

    /// <summary>月房租水电（元）。</summary>
    public decimal MonthlyRent { get; set; } = 2500m;

    /// <summary>日均通勤 + 餐饮费用（元）。</summary>
    public decimal DailyCommuteFood { get; set; } = 60m;

    /// <summary>每月发薪日（1~31）。</summary>
    public int PayDay { get; set; } = 15;

    /// <summary>月度白嫖忍耐上限（元），用于月 / 年维度熔断警报。</summary>
    public decimal MonthlyToleranceLimit { get; set; } = 800m;

    /// <summary>平日义务加班基础倍率（默认 1.0）。</summary>
    public decimal OvertimeRateNormal { get; set; } = 1.0m;

    /// <summary>平日义务加班超时分段倍率（默认 1.5）。</summary>
    public decimal OvertimeRateNormalLate { get; set; } = 1.5m;

    /// <summary>节假日义务加班基础倍率（默认 2.0）。</summary>
    public decimal OvertimeRateHoliday { get; set; } = 2.0m;

    /// <summary>节假日义务加班超时分段倍率（默认 3.0，法定节假日暴击）。</summary>
    public decimal OvertimeRateHolidayLate { get; set; } = 3.0m;

    /// <summary>平日加班倍率切换门槛（小时，超过后进入 late 倍率）。</summary>
    public double NormalOvertimeStageHours { get; set; } = 2.0;

    /// <summary>节假日加班倍率切换门槛（小时）。</summary>
    public double HolidayOvertimeStageHours { get; set; } = 8.0;

    /// <summary>
    /// 旧版单字段加班基数（元/月），仅用于从旧配置迁移。
    /// 新配置请使用 <see cref="UseCustomOvertimeBase"/> + <see cref="OvertimeBaseSalary"/>。
    /// </summary>
    public decimal OvertimeBaseMonthly { get; set; }

    /// <summary>是否启用「合同底薪 / 当地最低底薪」作为加班费基数。</summary>
    public bool UseCustomOvertimeBase { get; set; }

    /// <summary>加班基准底薪（元/月），默认 2360；仅在开关开启时生效。</summary>
    public decimal OvertimeBaseSalary { get; set; } = 2360m;

    /// <summary>老板键行为：彻底隐身 / PLC 伪装 / Excel 伪装。</summary>
    public BossKeyMode BossKeyMode { get; set; } = BossKeyMode.Stealth;

    /// <summary>老板键是否处于激活状态；重启后按这个状态恢复，只有主动切换才改变。</summary>
    public bool BossKeyActive { get; set; }

    /// <summary>是否随 Windows 登录自动启动（写入 HKCU Run，仅当前用户）。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>首次使用日期；自动补齐不会补到这一天之前，避免新用户收到安装前的假记录。</summary>
    public DateTime? FirstRunDate { get; set; }

    /// <summary>全局老板键组合（固定 Alt+Z，保留字段便于提示）。</summary>
    public string BossHotKey { get; set; } = "Alt+Z";

    /// <summary>启动时是否显示灵动岛。</summary>
    public bool ShowIslandOnStart { get; set; } = true;

    /// <summary>看板日历图例是否显示（点 × 关闭后持久化）。</summary>
    public bool ShowCalendarLegend { get; set; } = true;

    /// <summary>关闭主窗口时是否收进托盘（默认 true）。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// 每日标准有效工时 H：由「上班 / 下班 / 午休」自动折算，不再手填。
    /// 计算方式：下班 - 上班 - 与工作区间重叠的午休时长；异常输入时回退到旧值。
    /// </summary>
    [JsonIgnore]
    public double ComputedStandardHours
    {
        get
        {
            if (!TryParseClock(WorkStart, out var start) ||
                !TryParseClock(WorkEnd, out var end) ||
                end <= start)
            {
                return Math.Max(0.5, StandardHours);
            }

            var hours = (end - start).TotalHours;

            // 午休只扣除与工作区间真正重叠的部分，防止用户把午休填到工作时间之外
            if (TryParseClock(LunchStart, out var lunchStart) &&
                TryParseClock(LunchEnd, out var lunchEnd) &&
                lunchEnd > lunchStart)
            {
                var overlapStart = lunchStart > start ? lunchStart : start;
                var overlapEnd = lunchEnd < end ? lunchEnd : end;
                if (overlapEnd > overlapStart)
                {
                    hours -= (overlapEnd - overlapStart).TotalHours;
                }
            }

            return Math.Round(Math.Max(0.5, hours), 4);
        }
    }

    /// <summary>解析 HH:mm 或 H:mm。</summary>
    private static bool TryParseClock(string? text, out TimeSpan value)
        => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value) &&
           value >= TimeSpan.Zero && value < TimeSpan.FromDays(1);

    /// <summary>
    /// 判断某天是否为工作日。
    /// 优先级：补班日 &gt; 停计假日 &gt; 今日休假 &gt; 手选常规休息 &gt; 休息制度规则。
    /// </summary>
    public bool IsWorkday(DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var calendar = HolidayCalendar;

        if (calendar.Workdays.Contains(key)) return true;
        if (calendar.Holidays.ContainsKey(key)) return false;
        if (TodayOff && TodayOffDate == key) return false;
        if (calendar.RegularRestDays.Contains(key)) return false;

        return RestSystem switch
        {
            RestSystem.NoRest => true,
            RestSystem.DoubleWeekend => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
            RestSystem.BigSmallWeek => IsBigSmallWeekWorkday(date),
            // 月休 2/4 不预设周末规则：没手选休息日就按上班算
            _ => true
        };
    }

    /// <summary>月休 2/4 的每月休息名额；其他制度返回 0。</summary>
    [JsonIgnore]
    public int MonthlyRestQuota => RestSystem switch
    {
        RestSystem.Rest2Days => 2,
        RestSystem.Rest4Days => 4,
        _ => 0
    };

    /// <summary>取某天所在自然周的周一。</summary>
    public static DateTime WeekMonday(DateTime date)
        => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>判断某周是否为大周（大周=周六日休 2 天，小周=仅周日休 1 天）。</summary>
    public bool IsBigWeek(DateTime date)
    {
        var anchor = HolidayCalendar.BigSmallWeekAnchorMonday ?? WeekMonday(date);
        var weeks = (WeekMonday(date) - anchor.Date).Days / 7;
        var parity = ((weeks % 2) + 2) % 2;
        return parity == 0
            ? HolidayCalendar.BigSmallWeekAnchorIsBig
            : !HolidayCalendar.BigSmallWeekAnchorIsBig;
    }

    /// <summary>大小休的实际工作日判定：周日必休；大周周六休，小周周六上班。</summary>
    private bool IsBigSmallWeekWorkday(DateTime date)
    {
        if (date.DayOfWeek == DayOfWeek.Sunday) return false;
        if (date.DayOfWeek == DayOfWeek.Saturday) return !IsBigWeek(date);
        return true;
    }

    /// <summary>判断某天是否配置了停计假日。</summary>
    public bool IsHoliday(DateTime date)
        => HolidayCalendar.Holidays.ContainsKey(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>折算月有效计薪天数 D（只读派生）。</summary>
    [JsonIgnore]
    public decimal PayDays => RestSystem.PayDays();

    /// <summary>日薪（只读派生）。月薪：月薪 / D；时薪：时薪 × 自动折算的 H。</summary>
    [JsonIgnore]
    public decimal DailySalary => SalaryMode == SalaryMode.Monthly
        ? SafeDivide(SalaryAmount, PayDays)
        : SalaryAmount * (decimal)Math.Max(0.5, ComputedStandardHours);

    /// <summary>时薪（只读派生）。月薪：日薪 / H；时薪：原值。</summary>
    [JsonIgnore]
    public decimal HourlySalary => SalaryMode == SalaryMode.Monthly
        ? SafeDivide(DailySalary, (decimal)Math.Max(0.5, ComputedStandardHours))
        : SalaryAmount;

    /// <summary>
    /// 加班费基数时薪：
    /// 开启自定义底薪时按「底薪 ÷（月计薪天数 D × 每日工时 H）」；
    /// 关闭时按当前实际时薪（月薪模式等价于全额月薪 ÷ D ÷ H）。
    /// </summary>
    [JsonIgnore]
    public decimal OvertimeBaseHourly => UseCustomOvertimeBase
        ? SafeDivide(OvertimeBaseSalary,
            PayDays * (decimal)Math.Max(0.5, ComputedStandardHours))
        : HourlySalary;

    /// <summary>加班基数秒薪（元/秒），用于义务加班与夺命 Call 的亏损累计。</summary>
    [JsonIgnore]
    public decimal OvertimeBaseSecond => OvertimeBaseHourly / 3600m;

    /// <summary>分薪（只读派生）。</summary>
    [JsonIgnore]
    public decimal MinuteSalary => HourlySalary / 60m;

    /// <summary>秒薪（只读派生）。所有「每秒跳动」的数字都由它驱动。</summary>
    [JsonIgnore]
    public decimal SecondSalary => HourlySalary / 3600m;

    /// <summary>每日生存回本线 = 月房租水电 / 30 + 日均通勤餐饮。</summary>
    [JsonIgnore]
    public decimal DailySurvivalLine => Math.Round(SafeDivide(MonthlyRent, 30m) + DailyCommuteFood, 2);

    /// <summary>安全除法，避免 D 或 H 被填成 0 时抛异常。</summary>
    private static decimal SafeDivide(decimal a, decimal b) => b <= 0m ? 0m : a / b;
}

#endregion

#region 账本记录

/// <summary>单种摸鱼姿势的累计统计（日记录内使用）。</summary>
public sealed class SlackingStat
{
    /// <summary>摸鱼姿势。</summary>
    public SlackingType Type { get; set; }

    /// <summary>累计秒数。</summary>
    public double Seconds { get; set; }

    /// <summary>累计白嫖金额（元）。</summary>
    public decimal Amount { get; set; }
}

/// <summary>一次摸鱼会话的起止区间，用于回填下班时间时精确裁剪。</summary>
public sealed class SlackingSession
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public SlackingType Type { get; set; }
    public double Seconds { get; set; }
}

/// <summary>一次完整的夺命 Call 会话；解决同一天混合有偿/无偿时只保留最后模式的问题。</summary>
public sealed class EmergencyCallSession
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public EmergencyCallCompensationMode Mode { get; set; }
    public double Seconds { get; set; }
    public decimal Loss { get; set; }
    public decimal Earned { get; set; }
    public decimal Rate { get; set; }
    public decimal FixedSubsidy { get; set; }
    public decimal HourlyRate { get; set; }
}

/// <summary>
/// 每日账本记录：一天的正常收益、摸鱼白嫖、义务加班亏损、抹零损失与摸鱼明细。
/// 所有金额均为当日累计值，由 ViewModel 每 30 秒回写并持久化。
/// </summary>
public sealed class DailyRecord
{
    /// <summary>日期，格式 yyyy-MM-dd（主键）。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>正常计薪收益（元）。时薪模式下为已锁定金额。</summary>
    public decimal NormalEarned { get; set; }

    /// <summary>摸鱼白嫖金额（元，属于 NormalEarned 的子集）。</summary>
    public decimal SlackingEarned { get; set; }

    /// <summary>义务加班亏损金额（元，含夺命 Call 的白嫖亏损）。</summary>
    public decimal OvertimeLoss { get; set; }

    /// <summary>夺命 Call 累计亏损（元，属于 <see cref="OvertimeLoss"/> 的子集）。</summary>
    public decimal EmergencyCallLoss { get; set; }

    /// <summary>夺命 Call 累计实得收益（元，含固定补贴 / 按时薪折算，属于 NormalEarned 的子集）。</summary>
    public decimal EmergencyCallEarned { get; set; }

    /// <summary>夺命 Call 累计秒数（已包含在 <see cref="OvertimeSeconds"/> 里）。</summary>
    public double EmergencyCallSeconds { get; set; }

    /// <summary>夺命 Call 次数。</summary>
    public int EmergencyCallCount { get; set; }

    /// <summary>夺命 Call 的报酬方式。</summary>
    public EmergencyCallCompensationMode EmergencyCallMode { get; set; } = EmergencyCallCompensationMode.Unpaid;

    /// <summary>夺命 Call 的亏损倍率（2.0 / 3.0；有偿模式为 0）。</summary>
    public decimal EmergencyCallRate { get; set; }

    /// <summary>本次 / 当天夺命 Call 是否仍在进行中。</summary>
    public bool EmergencyCallActive { get; set; }

    /// <summary>夺命 Call 开始时间（用于跨天连续计时与重启恢复）。</summary>
    public DateTime? EmergencyCallStartedAt { get; set; }

    /// <summary>夺命 Call 结束时间。</summary>
    public DateTime? EmergencyCallEndedAt { get; set; }

    /// <summary>固定补贴金额（配置快照，便于重启恢复）。</summary>
    public decimal EmergencyCallFixedSubsidy { get; set; }

    /// <summary>实际加班时薪（配置快照，便于重启恢复）。</summary>
    public decimal EmergencyCallHourlyRate { get; set; }

    /// <summary>时薪抹零损失（元）。</summary>
    public decimal RoundingLoss { get; set; }

    /// <summary>有效计薪秒数。</summary>
    public double WorkedSeconds { get; set; }

    /// <summary>撤回下班后排除的不计薪空档秒数；持久化后重启仍不会被补算。</summary>
    public double WallClockExcludedSeconds { get; set; }

    /// <summary>摸鱼累计秒数。</summary>
    public double SlackingSeconds { get; set; }

    /// <summary>义务加班累计秒数。</summary>
    public double OvertimeSeconds { get; set; }
    /// <summary>时薪模式下已锁定的到手金额（元）。</summary>
    public decimal LockedHourlyAmount { get; set; }

    /// <summary>当天开始摸鱼的次数（每次点「开始摸鱼」+1）。</summary>
    public int SlackingCount { get; set; }

    /// <summary>时薪模式下下班时被抹零的秒数（未满半点部分）。</summary>
    public double RoundingLostSeconds { get; set; }

    /// <summary>当天是否为节假日 / 休息日。</summary>
    public bool IsHoliday { get; set; }

    /// <summary>当天是否已确认下班。</summary>
    public bool IsOffDuty { get; set; }

    /// <summary>是否为自动补齐 / 自动下班生成的记录。</summary>
    public bool AutoFilled { get; set; }

    /// <summary>是否发生过夺命 Call。</summary>
    [JsonIgnore]
    public bool HasEmergencyCall => EmergencyCallCount > 0 || EmergencyCallSeconds > 0.5 ||
                                    EmergencyCallLoss > 0m || EmergencyCallEarned > 0m;

    /// <summary>夺命 Call 是否为有偿模式。</summary>
    [JsonIgnore]
    public bool IsEmergencyCallPaid => EmergencyCallMode != EmergencyCallCompensationMode.Unpaid;

    /// <summary>夺命 Call 标签展示：[夺命Call] 时长 / 亏损或收益。</summary>
    [JsonIgnore]
    public string EmergencyCallDisplay
    {
        get
        {
            if (!HasEmergencyCall) return string.Empty;

            var duration = TimeSpan.FromSeconds(Math.Max(0, EmergencyCallSeconds));
            var text = $"[夺命Call] {duration:hh\\:mm\\:ss}";
            if (EmergencyCallEarned > 0m) text += $" · +¥{EmergencyCallEarned:F2}";
            if (EmergencyCallLoss > 0m) text += $" · -¥{EmergencyCallLoss:F2}";
            if (EmergencyCallActive) text += " · 进行中";
            return text;
        }
    }

    /// <summary>确认下班的时间点；用于事后按当前薪资重算加班与抹零。</summary>
    public DateTime? ClockOutAt { get; set; }

    /// <summary>当天是否启动过「节假日被迫义务加班」。</summary>
    public bool HolidayOvertime { get; set; }

    /// <summary>节假日被迫义务加班的开始时间（关闭软件再打开可继续累计）。</summary>
    public DateTime? HolidayOvertimeStart { get; set; }

    /// <summary>当前这次摸鱼的开始时间；为空表示没在摸鱼。</summary>
    public DateTime? SlackingStartedAt { get; set; }

    /// <summary>当前摸鱼姿势（配合 <see cref="SlackingStartedAt"/> 使用）。</summary>
    public SlackingType ActiveSlackingType { get; set; } = SlackingType.Phone;

    /// <summary>当天所有摸鱼会话区间；用于精确裁剪与重启恢复。</summary>
    public List<SlackingSession> SlackingSessions { get; set; } = new();

    /// <summary>当天所有夺命 Call 会话；每条独立保留报酬方式。</summary>
    public List<EmergencyCallSession> EmergencyCallSessions { get; set; } = new();

    /// <summary>各摸鱼姿势明细。</summary>
    public List<SlackingStat> SlackingStats { get; set; } = new();

    /// <summary>最后更新时间（用于排查与排序）。</summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>当天的「摸鱼 - 加班 - 抹零」净值。</summary>
    [JsonIgnore]
    public decimal HedgeNet => SlackingEarned - OvertimeLoss - RoundingLoss;

    /// <summary>当天的有效工时（小时，用于展示）。</summary>
    [JsonIgnore]
    public double WorkedHours => WorkedSeconds / 3600.0;

    /// <summary>正常收益展示文本。</summary>
    [JsonIgnore]
    public string NormalDisplay => "¥" + NormalEarned.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>摸鱼白嫖展示文本。</summary>
    [JsonIgnore]
    public string SlackingDisplay => "+ ¥" + SlackingEarned.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>义务加班亏损展示文本。</summary>
    [JsonIgnore]
    public string OvertimeDisplay => "- ¥" + OvertimeLoss.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>抹零损失展示文本。</summary>
    [JsonIgnore]
    public string RoundingDisplay => "- ¥" + RoundingLoss.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>当日对冲净值展示文本。</summary>
    [JsonIgnore]
    public string HedgeDisplay => (HedgeNet >= 0m ? "+ ¥" : "- ¥") +
                                 Math.Abs(HedgeNet).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>账本行状态标记。</summary>
    [JsonIgnore]
    public string FlagDisplay => AutoFilled
        ? "🤖 自动补齐"
        : EmergencyCallActive
            ? "🚨 夺命Call"
            : IsHoliday ? "🏖️ 休假" : IsOffDuty ? "✅ 已下班" : "· 计薪中";
}

/// <summary>历史账本文件根对象。</summary>
public sealed class Ledger
{
    /// <summary>账本结构版本。</summary>
    public int Version { get; set; } = 1;

    /// <summary>每日记录集合（按日期唯一）。</summary>
    public List<DailyRecord> Records { get; set; } = new();
}

/// <summary>确认下班时间弹窗所需上下文。</summary>
public sealed class ClockOutTimeContext
{
    /// <summary>当前业务日。</summary>
    public DateTime BusinessDate { get; init; }

    /// <summary>当日上班时间。</summary>
    public DateTime WorkStart { get; init; }

    /// <summary>当日下班时间。</summary>
    public DateTime WorkEnd { get; init; }

    /// <summary>允许选择的最早时间（节假日加班时是加班开始时间）。</summary>
    public DateTime Earliest { get; init; }

    /// <summary>当前真实时间，不能选未来。</summary>
    public DateTime Now { get; init; }

    /// <summary>是否正在节假日被迫加班。</summary>
    public bool HolidayOvertime { get; init; }
}

#endregion

#region 聚合结果

/// <summary>某一种摸鱼姿势在统计周期内的占比数据。</summary>
public sealed class SlackingSlice
{
    /// <summary>摸鱼姿势。</summary>
    public SlackingType Type { get; set; }

    /// <summary>累计秒数。</summary>
    public double Seconds { get; set; }

    /// <summary>累计金额（元）。</summary>
    public decimal Amount { get; set; }

    /// <summary>金额占比（0~100）。</summary>
    public double Percent { get; set; }

    /// <summary>带 emoji 的展示名。</summary>
    public string DisplayName => Type.DisplayName();

    /// <summary>占比文案，如 65.0%。</summary>
    public string PercentText => Percent.ToString("F1") + "%";

    /// <summary>金额文案。</summary>
    public string AmountText => "¥" + Amount.ToString("F2");
}

/// <summary>
/// 日 / 周 / 月 / 年聚合结果：供复盘看板一次性绑定，避免界面里散落聚合代码。
/// </summary>
public sealed class AggregateResult
{
    /// <summary>统计维度。</summary>
    public ViewMode Mode { get; set; }

    /// <summary>看板标题（如「本月财务复盘」）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>周期描述（如 2026-10-01 ~ 2026-10-31）。</summary>
    public string RangeText { get; set; } = string.Empty;

    /// <summary>周期起始日期。</summary>
    public DateTime Start { get; set; }

    /// <summary>周期结束日期。</summary>
    public DateTime End { get; set; }

    /// <summary>有数据的天数。</summary>
    public int DayCount { get; set; }

    /// <summary>有效计薪总秒数。</summary>
    public double TotalPaidSeconds { get; set; }

    /// <summary>义务加班总秒数（按秒统计，不取整，含夺命 Call）。</summary>
    public double TotalOvertimeSeconds { get; set; }

    /// <summary>夺命 Call 总秒数（已含在 <see cref="TotalOvertimeSeconds"/> 中）。</summary>
    public double EmergencyCallSeconds { get; set; }

    /// <summary>夺命 Call 总亏损（已含在 <see cref="OvertimeTotal"/> 中）。</summary>
    public decimal EmergencyCallLoss { get; set; }

    /// <summary>夺命 Call 总有偿收益（已含在 <see cref="TotalIncome"/> 中）。</summary>
    public decimal EmergencyCallEarned { get; set; }

    /// <summary>夺命 Call 次数。</summary>
    public int EmergencyCallCount { get; set; }

    /// <summary>摸鱼总秒数。</summary>
    public double TotalSlackingSeconds { get; set; }

    /// <summary>真正有工时的天数（有效工时或义务加班 &gt; 0）。</summary>
    public int ActiveDayCount { get; set; }

    /// <summary>总工时秒数 = 有效计薪工时 + 义务加班工时。</summary>
    public double TotalWorkedSeconds => TotalPaidSeconds + TotalOvertimeSeconds;

    /// <summary>平均工时（小时/出勤天）。</summary>
    public double AverageWorkHours => ActiveDayCount <= 0
        ? 0
        : TotalWorkedSeconds / ActiveDayCount / 3600.0;

    /// <summary>周期内休息 / 节假日天数（按当前排班配置）。</summary>
    public int RestDayCount { get; set; }

    /// <summary>已结清每日生存线的天数。</summary>
    public int SettledSurvivalDays { get; set; }

    /// <summary>纯利润净额 = 累计实得工资 − 每日生存线 × 出勤天数。</summary>
    public decimal ProfitNet { get; set; }

    /// <summary>摸鱼总次数。</summary>
    public int SlackingCount { get; set; }

    /// <summary>抹零白干总秒数。</summary>
    public double RoundingLostSeconds { get; set; }

    /// <summary>统计时使用的每日生存线，供看板说明。</summary>
    public decimal DailySurvivalLine { get; set; }

    /// <summary>总收入（正常计薪，含摸鱼）。</summary>
    public decimal TotalIncome { get; set; }

    /// <summary>摸鱼白嫖总额。</summary>
    public decimal SlackingTotal { get; set; }

    /// <summary>义务加班血亏总额。</summary>
    public decimal OvertimeTotal { get; set; }

    /// <summary>抹零损失总额。</summary>
    public decimal RoundingTotal { get; set; }

    /// <summary>摸鱼姿势成分。</summary>
    public List<SlackingSlice> Slices { get; set; } = new();

    /// <summary>熔断阈值（月：配置上限；年：上限 × 12；日 / 周：0 表示不告警）。</summary>
    public decimal ToleranceLimit { get; set; }

    /// <summary>是否触发熔断警报。</summary>
    public bool Alert => ToleranceLimit > 0m && OvertimeTotal > ToleranceLimit;

    /// <summary>对冲净值 = 摸鱼白嫖 - (义务加班 + 抹零损失)。</summary>
    public decimal HedgeNet => SlackingTotal - (OvertimeTotal + RoundingTotal);
}

#endregion
