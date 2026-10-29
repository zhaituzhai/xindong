using System;
#pragma warning disable CS8600, CS8619
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XinDong.Models;
using XinDong.Services;

namespace XinDong.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
	private const double StepSeconds = 1800.0;

	private const double SlackWarnSeconds = 900.0;

	public const int PageToday = 0;

	public const int PageBoard = 1;

	public const int PageLedger = 2;

	public const int PageSettings = 3;

	public const int PageRank = 4;

	private static readonly Brush BrushBlue = MakeBrush("#007AFF");

	private static readonly Brush BrushBlueSoft = MakeSoftBrush("#007AFF");

	private static readonly Brush BrushGreen = MakeBrush("#30D158");

	private static readonly Brush BrushGreenSoft = MakeSoftBrush("#30D158");

	private static readonly Brush BrushRed = MakeBrush("#FF453A");

	private static readonly Brush BrushRedSoft = MakeSoftBrush("#FF453A");

	private static readonly Brush BrushOrange = MakeBrush("#FF9F0A");

	private static readonly Brush BrushOrangeSoft = MakeSoftBrush("#FF9F0A");

	private static readonly Brush BrushGray = MakeBrush("#8E8E93");

	private static readonly Brush BrushGraySoft = MakeSoftBrush("#8E8E93");

	private static readonly Brush BrushInk = MakeBrush("#1C1C1E");

	private static readonly Brush BrushWhite = MakeBrush("#FFFFFF");

	private static readonly Brush BrushHeroSubDark = MakeBrush("#C7C7CC");

	private static readonly Brush BrushZero = MakeBrush("#C7C7CC");

	private static readonly Brush BrushLabel = MakeBrush("#6C6C70");

	private static readonly Brush BrushHolidaySoft = MakeBrush("#FFF1F0");

	private static readonly Brush BrushRestSoft = MakeBrush("#F5F5F7");

	private static readonly Brush BrushWorkSoft = MakeBrush("#F2F8FF");

	private static readonly string[] LivePropertyNames = (from p in typeof(MainViewModel).GetProperties(BindingFlags.Instance | BindingFlags.Public)
		where p.Name != "Config" && p.Name != "Slices" && p.Name != "History" && p.PropertyType != typeof(OptionItem) && !typeof(ICommand).IsAssignableFrom(p.PropertyType)
		select p.Name).ToArray();

	private readonly StorageService _storage;

	private readonly Ledger _ledger;

	private readonly HolidayService _holidayService = new HolidayService();

	private readonly DispatcherTimer _timer;

	private readonly bool _hadHistory;

	private DailyRecord _today;

	private DateTime _sessionDate;

	private DateTime _lastTick;

	private DateTime _lastSave;

	private DateTime _lastBoardRefresh;

	private long _lastSecondKey;

	private WorkState _state;

	private bool _offDuty;

	private bool _slacking;

	private SlackingType _slackType = SlackingType.Phone;

	private double _slackSessionSeconds;

	private bool _slackWarned;

	private bool _holidayOvertime;

	private DateTime _holidayStart = DateTime.Now;

	private bool _emergency;

	private DateTime _emergencyStart;

	private EmergencyCallOptions _emergencyOptions = new EmergencyCallOptions();

	private double _emergencySeconds;

	private decimal _emergencyRate;

	private double _wallClockExcludedSeconds;

	private bool _off5Notified;

	private bool _offAtNotified;

	private bool _forceRefresh = true;

	private bool _camouflage;

	private bool _disposed;

	private int _page;

	private ViewMode _viewMode;

	private AggregateResult _aggregate = new AggregateResult();

	private DateTime _boardDate = DateTime.Today;

	private bool _calendarExpanded;

	private bool _detailOpen;

	private bool _analysisExpanded;

	private DateTime _boardUpdatedAt = DateTime.Now;

	private DateTime _weekChipsMonth = DateTime.MinValue;

	private int _monthChipsYear = int.MinValue;

	private int _yearPageStart = DateTime.Today.Year - 4;

	private OptionItem _selectedSalaryMode;

	private OptionItem _selectedRest;

	private OptionItem _selectedBoss;

	private int _holidayYear = DateTime.Today.Year;

	private int _holidayMonth = DateTime.Today.Month;

	private string _holidaySyncMessage = string.Empty;

	private Brush _holidaySyncBrush = BrushGreen;

	private bool _isHolidaySyncing;

	private string _settingsMessage = string.Empty;

	private string _hotkeyStatusText = "全局老板键：Alt + Z（等待注册结果…）";

	private int _overtimeQuoteIndex;

	private DateTime _lastQuoteAt = DateTime.Now;

	private static readonly string[] OvertimeQuotes = new string[5] { "你的时薪，正在被会议室一口一口吃掉。", "老板的新车，可能就有你今晚这一小时。", "再坐一会儿，他的年终奖又厚了一页。", "免费加班不是福报，是一张不会报销的账单。", "你现在的时间，正以 1.5 倍速变成别人的利润。" };

	public UserConfig Config { get; }

	public IReadOnlyList<OptionItem> SalaryModeOptions { get; }

	public IReadOnlyList<OptionItem> RestOptions { get; }

	public IReadOnlyList<OptionItem> BossOptions { get; }

	public WorkState State => _state;

	public string DateText => _sessionDate.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));

	public string ClockText => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

	public decimal TodayEarned
	{
		get
		{
			double num = Math.Max(0.0, _today.WorkedSeconds);
			decimal num3;
			if (Config.SalaryMode == SalaryMode.Hourly)
			{
				decimal num2 = LockedAmount(num);
				num3 = (_offDuty ? num2 : (num2 + PartialAmount(num)));
			}
			else
			{
				decimal num4 = (decimal)Math.Floor(num) * Config.SecondSalary;
				num3 = ((Config.DailySalary > 0m) ? Math.Min(num4, Config.DailySalary) : num4);
			}
			decimal emergencyCallEarned = _today.EmergencyCallEarned;
			if (_emergency && _emergencyOptions.Mode == EmergencyCallCompensationMode.HourlyRate)
			{
				emergencyCallEarned += EmergencyEarnedLive;
			}
			return num3 + emergencyCallEarned;
		}
	}

	public string EarnedNumberText => Money(TodayEarned, 4);

	public double EmergencySeconds
	{
		get
		{
			if (!_emergency)
			{
				return _today.EmergencyCallSeconds;
			}
			return _emergencySeconds;
		}
	}

	public decimal BossOwedEmergencyFee
	{
		get
		{
			if (!_emergency)
			{
				return _today.EmergencyCallLoss;
			}
			if (_emergencyOptions.Mode != EmergencyCallCompensationMode.Unpaid)
			{
				return 0m;
			}
			return (decimal)_emergencySeconds * Config.OvertimeBaseSecond * _emergencyRate;
		}
	}

	public decimal EmergencyEarnedLive
	{
		get
		{
			if (!_emergency || _emergencyOptions.Mode != EmergencyCallCompensationMode.HourlyRate)
			{
				return 0m;
			}
			return (decimal)_emergencySeconds * _emergencyOptions.HourlyRate / 3600m;
		}
	}

	public decimal EmergencyRate
	{
		get
		{
			if (!_emergency)
			{
				return _today.EmergencyCallRate;
			}
			return _emergencyRate;
		}
	}

	public string EmergencyIslandText
	{
		get
		{
			if (!_emergency)
			{
				return string.Empty;
			}
			string value = Duration(_emergencySeconds);
			return _emergencyOptions.Mode switch
			{
				EmergencyCallCompensationMode.FixedSubsidy => $"夺命Call被迫营业中: 补贴 ¥{Money(_emergencyOptions.FixedSubsidy, 2)} (已干 {value})", 
				EmergencyCallCompensationMode.HourlyRate => $"夺命Call被迫营业中: +¥{Money(EmergencyEarnedLive, 2)} (已干 {value})", 
				_ => $"夺命Call被迫营业中: -¥{Money(BossOwedEmergencyFee, 2)} (已白干 {value})", 
			};
		}
	}

	public string EmergencyCallModeText
	{
		get
		{
			if (_emergency)
			{
				return _emergencyOptions.Mode switch
				{
					EmergencyCallCompensationMode.FixedSubsidy => "固定补贴 ¥" + Money(_emergencyOptions.FixedSubsidy, 2), 
					EmergencyCallCompensationMode.HourlyRate => "按时薪 ¥" + Money(_emergencyOptions.HourlyRate, 2) + "/小时", 
					_ => $"纯义务白嫖 · {_emergencyRate:0.0}× 亏损", 
				};
			}
			return string.Empty;
		}
	}

	public double DisplayOvertimeSeconds => _today.OvertimeSeconds + (_emergency ? _emergencySeconds : 0.0);

	public decimal CurrentActualHourly
	{
		get
		{
			double num = Math.Max(0.5, Config.ComputedStandardHours) + DisplayOvertimeSeconds / 3600.0;
			if (!(num <= 0.0))
			{
				return Config.DailySalary / (decimal)num;
			}
			return 0m;
		}
	}

	public decimal HourlyDepreciationPercent
	{
		get
		{
			decimal hourlySalary = Config.HourlySalary;
			if (hourlySalary <= 0m)
			{
				return 0m;
			}
			return Math.Clamp((1m - CurrentActualHourly / hourlySalary) * 100m, 0m, 100m);
		}
	}

	public string CurrentActualHourlyText => "¥" + Money(CurrentActualHourly, 2);

	public string HourlyDepreciationText => "↓ " + Money(HourlyDepreciationPercent, 1) + "%";

	public string HourlyDepreciationHint => $"今日：固定日薪 ¥{Money(Config.DailySalary, 2)} ÷（{Config.ComputedStandardHours:0.##}h + 加班 {Duration(DisplayOvertimeSeconds)}）";

	public string SecondSalaryText
	{
		get
		{
			string text = ((Config.SalaryMode == SalaryMode.Hourly) ? " · 理论秒薪" : string.Empty);
			return "每秒 + ¥" + Money(Config.SecondSalary, 4) + text;
		}
	}

	public string LockedText => "已锁定 ¥" + Money(LockedAmount(_today.WorkedSeconds), 2);

	public string CountdownText
	{
		get
		{
			double num = Math.Floor(Math.Max(0.0, _today.WorkedSeconds));
			return Clock(1800.0 - num % 1800.0);
		}
	}

	public double CountdownPercent => Math.Clamp(Math.Max(0.0, _today.WorkedSeconds) % 1800.0 / 1800.0 * 100.0, 0.0, 100.0);

	public string SlackingAmountText => "+ ¥" + Money(SlackingAmount, 4);

	public decimal SlackingAmount => (decimal)Math.Max(0.0, _today.SlackingSeconds) * Config.SecondSalary;

	public string SlackingTimeText => "本次 " + Duration(_slackSessionSeconds) + " · 今日 " + Duration(_today.SlackingSeconds);

	public string SlackingBenefitText => ConvertToGoods(SlackingAmount);

	public decimal LiveOvertimeLoss => _today.OvertimeLoss + ((_emergency && _emergencyOptions.Mode == EmergencyCallCompensationMode.Unpaid) ? BossOwedEmergencyFee : 0m);

	public string OvertimeAmountText => "- ¥" + Money(LiveOvertimeLoss, 4);

	public string OvertimeTimeText => "免费加班 " + Duration(DisplayOvertimeSeconds);

	public string OvertimeGoodsText
	{
		get
		{
			if (!(LiveOvertimeLoss <= 0m))
			{
				return "≈ " + (LiveOvertimeLoss / 18m).ToString("F1") + " 顿猪脚饭被老板端走";
			}
			return "还没被白嫖，继续保持";
		}
	}

	public string OvertimeQuoteText
	{
		get
		{
			if (!IsOvertime)
			{
				return "下班时间一到，记得立刻站起来。";
			}
			return OvertimeQuotes[_overtimeQuoteIndex % OvertimeQuotes.Length];
		}
	}

	public string OvertimeRateText
	{
		get
		{
			if (_emergency)
			{
				if (_emergencyOptions.Mode != EmergencyCallCompensationMode.Unpaid)
				{
					return "夺命Call 有偿营业中";
				}
				return $"夺命Call {_emergencyRate:0.0}× 暴击中";
			}
			if (_state != WorkState.Overtime && !_holidayOvertime)
			{
				return string.Empty;
			}
			decimal value = CurrentOvertimeRate();
			string value2 = (_holidayOvertime ? "节假日" : "平日");
			return $"{value2} {value:0.0}× 白嫖中";
		}
	}

	public double TotalWorkedSeconds => Math.Max(0.0, _today.WorkedSeconds) + DisplayOvertimeSeconds;

	public string WorkedTimeText => "今日工时 " + Duration(TotalWorkedSeconds);

	public string WorkedDurationText => Duration(TotalWorkedSeconds);

	public string WorkedBreakdownText => $"有效计薪 {Duration(_today.WorkedSeconds)} · 义务加班 {Duration(_today.OvertimeSeconds)}（按秒计，不取整）";

	public string RemainingWorkText
	{
		get
		{
			DateTime now = DateTime.Now;
			if (_emergency)
			{
				return "夺命Call进行中 · " + Duration(_emergencySeconds);
			}
			if (IsHolidayToday && !_holidayOvertime)
			{
				return "节假日停计 · 安心躺平";
			}
			if (_offDuty)
			{
				return "今日已下班 · 拒绝白嫖";
			}
			if (_holidayOvertime)
			{
				return "节假日义务加班 " + Duration(_today.OvertimeSeconds);
			}
			if (_state == WorkState.Overtime)
			{
				return "已义务加班 " + Duration(_today.OvertimeSeconds);
			}
			DateTime dateTime = At(_sessionDate, Config.WorkEnd);
			if (!(now < dateTime))
			{
				return "已到下班时间";
			}
			return "距离下班 " + Duration((dateTime - now).TotalSeconds);
		}
	}

	public double SurvivalPercent
	{
		get
		{
			decimal dailySurvivalLine = Config.DailySurvivalLine;
			if (dailySurvivalLine <= 0m)
			{
				return 100.0;
			}
			return Math.Clamp((double)(TodayEarned / dailySurvivalLine * 100m), 0.0, 100.0);
		}
	}

	public decimal TodayNetProfit => TodayEarned - Config.DailySurvivalLine - LiveOvertimeLoss - _today.RoundingLoss;

	public string SurvivalText
	{
		get
		{
			decimal dailySurvivalLine = Config.DailySurvivalLine;
			decimal todayEarned = TodayEarned;
			decimal todayNetProfit = TodayNetProfit;
			if (todayEarned >= dailySurvivalLine)
			{
				if (!(todayNetProfit >= 0m))
				{
					return "⚠\ufe0f 生存成本已回本，但义务加班与抹零让今天倒贴 ¥" + Money(-todayNetProfit, 2);
				}
				return "\ud83c\udf89 今日生存成本已结清！扣除加班与抹零后仍净赚 ¥" + Money(todayNetProfit, 2);
			}
			double value = ((dailySurvivalLine <= 0m) ? 100.0 : ((double)(todayEarned / dailySurvivalLine * 100m)));
			decimal num = LiveOvertimeLoss + _today.RoundingLoss;
			string value2 = ((num > 0m) ? ("，另有加班/抹零亏损 ¥" + Money(num, 2)) : string.Empty);
			return $"\ud83c\udfda\ufe0f 正在替房东和外卖平台打工（回本进度 {value:F1}%{value2}）";
		}
	}

	public string SurvivalNetText
	{
		get
		{
			decimal todayNetProfit = TodayNetProfit;
			if (todayNetProfit >= 0m)
			{
				return "净赚 ¥" + Money(todayNetProfit, 2);
			}
			return ((TodayEarned < Config.DailySurvivalLine) ? "还差 ¥" : "倒贴 ¥") + Money(-todayNetProfit, 2);
		}
	}

	public bool IsSurvivalCleared => TodayEarned >= Config.DailySurvivalLine;

	public bool IsHourly => Config.SalaryMode == SalaryMode.Hourly;

	public bool IsMonthly => Config.SalaryMode == SalaryMode.Monthly;

	public bool IsWorking => _state == WorkState.Working;

	public bool IsSlacking => _state == WorkState.Slacking;

	public bool IsOvertime
	{
		get
		{
			if (_state != WorkState.Overtime && !_holidayOvertime)
			{
				return _emergency;
			}
			return true;
		}
	}

	public bool IsLunch => _state == WorkState.LunchBreak;

	public bool IsOffDuty => _state == WorkState.OffDuty;

	public bool IsNotStarted => _state == WorkState.NotStarted;

	public bool IsHolidayToday => !IsWorkday(_sessionDate);

	public bool IsTodayOff
	{
		get
		{
			if (Config.TodayOff)
			{
				return Config.TodayOffDate == TodayKey;
			}
			return false;
		}
		set
		{
			if (value != IsTodayOff)
			{
				SetTodayOff(value);
			}
		}
	}

	public bool IsHolidayOvertimeActive => _holidayOvertime;

	public string StatusText => _state switch
	{
		WorkState.Working => "\ud83d\udcbc 正常搬砖中，每滴汗水都在计价", 
		WorkState.Slacking => "\ud83d\udc1f " + _slackType.DisplayName() + " 中，工资照跳，舒服", 
		WorkState.LunchBreak => "⏸ 午休暂停造富中", 
		WorkState.OffDuty => "✅ 今日已下班，拒绝免费加班", 
		WorkState.Holiday => _holidayOvertime ? "\ud83e\ude78 节假日被迫义务加班中，老板正在白嫖你" : "\ud83c\udfd6\ufe0f 节假日停止计费中（远离工位，安心躺平）", 
		WorkState.Overtime => "\ud83e\ude78 义务加班中，你的时间正在被免费拿走", 
		WorkState.EmergencyOvertime => "\ud83d\udea8 老板夺命Call，正在被迫营业", 
		_ => "\ud83c\udf24 还没到上班时间，先攒点力气", 
	};

	public string IslandStateText => _state switch
	{
		WorkState.Working => "搬砖中", 
		WorkState.Slacking => "摸鱼中", 
		WorkState.LunchBreak => "午休中", 
		WorkState.OffDuty => "已下班", 
		WorkState.Holiday => _holidayOvertime ? "被迫加班" : "休假中", 
		WorkState.Overtime => "义务加班", 
		WorkState.EmergencyOvertime => "夺命Call", 
		_ => "未上班", 
	};

	public string IslandAmountText => "¥" + Money(TodayEarned, 4);

	public string IslandSlackText => "摸鱼 +" + Money(SlackingAmount, 2);

	public string IslandOvertimeText => "加班 -" + Money(LiveOvertimeLoss, 2);

	public string IslandSubText => Duration(_today.WorkedSeconds + DisplayOvertimeSeconds);

	public string IslandFishToolTip
	{
		get
		{
			if (!IsCamouflage)
			{
				if (!IsSlacking)
				{
					return "开始 / 结束带薪摸鱼";
				}
				return "本次摸鱼 " + Duration(_slackSessionSeconds);
			}
			if (!IsSlacking)
			{
				return "运行控制";
			}
			return "运行中（再点停止）";
		}
	}

	public Brush IslandFishBrush
	{
		get
		{
			if (!IsSlacking)
			{
				return Brushes.Transparent;
			}
			return BrushGreen;
		}
	}

	public Brush IslandMiniDotBrush
	{
		get
		{
			if (!IsCamouflage)
			{
				return StatusBrush;
			}
			if (!IsSlacking)
			{
				return BrushBlue;
			}
			return BrushGreen;
		}
	}

	public Brush StatusBrush => _state switch
	{
		WorkState.Working => BrushBlue, 
		WorkState.Slacking => BrushGreen, 
		WorkState.LunchBreak => BrushOrange, 
		WorkState.Overtime => BrushRed, 
		WorkState.EmergencyOvertime => BrushRed, 
		WorkState.Holiday => _holidayOvertime ? BrushRed : BrushGray, 
		_ => BrushGray, 
	};

	public Brush StatusSoftBrush => _state switch
	{
		WorkState.Working => BrushBlueSoft, 
		WorkState.Slacking => BrushGreenSoft, 
		WorkState.LunchBreak => BrushOrangeSoft, 
		WorkState.Overtime => BrushRedSoft, 
		WorkState.EmergencyOvertime => BrushRedSoft, 
		WorkState.Holiday => _holidayOvertime ? BrushRedSoft : BrushGraySoft, 
		_ => BrushGraySoft, 
	};

	public Brush EarnedBrush
	{
		get
		{
			if (!IsOvertime)
			{
				if (_state != WorkState.Slacking)
				{
					return BrushBlue;
				}
				return BrushGreen;
			}
			return BrushRed;
		}
	}

	public Brush HeroTextBrush
	{
		get
		{
			if (!IsOvertime)
			{
				return BrushInk;
			}
			return BrushWhite;
		}
	}

	public Brush HeroSubTextBrush
	{
		get
		{
			if (!IsOvertime)
			{
				return BrushGray;
			}
			return BrushHeroSubDark;
		}
	}

	public bool CanStartSlack => _state == WorkState.Working;

	public bool CanStopSlack => _state == WorkState.Slacking;

	public bool CanClockOut
	{
		get
		{
			if (!_offDuty)
			{
				if (!IsWorkday(_sessionDate))
				{
					return _holidayOvertime;
				}
				return true;
			}
			return false;
		}
	}

	public bool CanUndoOffDuty
	{
		get
		{
			if (_offDuty)
			{
				return !_emergency;
			}
			return false;
		}
	}

	public bool CanResumeWork => CanUndoOffDuty;

	public bool CanTriggerEmergencyCall
	{
		get
		{
			if (!_emergency)
			{
				if (!_offDuty)
				{
					return IsHolidayToday;
				}
				return true;
			}
			return false;
		}
	}

	public bool ShowEmergencyCallButton => CanTriggerEmergencyCall;

	public bool ShowLeisureButtons
	{
		get
		{
			if (!_offDuty && !IsHolidayToday)
			{
				return !_emergency;
			}
			return false;
		}
	}

	public bool IsEmergencyCall => _emergency;

	public bool CanEndEmergencyCall => _emergency;

	public bool IsMonthlySalaryLocked
	{
		get
		{
			if (Config.SalaryMode == SalaryMode.Monthly)
			{
				if (!_offDuty && !_emergency)
				{
					return _state == WorkState.Overtime;
				}
				return true;
			}
			return false;
		}
	}

	public string SalaryLockText => "\ud83d\udd12 今日工资已封顶（月薪包干，加班老板不给一分钱）";

	public bool CanHolidayOvertime
	{
		get
		{
			if (IsHolidayToday && !_holidayOvertime)
			{
				return !_offDuty;
			}
			return false;
		}
	}

	public string SlackingTypeName => _slackType.DisplayName();

	public int CurrentPageIndex => _page;

	/// <summary>当前内存账本（供上报读取最新数据）。</summary>
	public Ledger CurrentLedger => _ledger;

	public bool IsPageToday => _page == 0;

	public bool IsPageBoard => _page == 1;

	public bool IsPageLedger => _page == 2;

	public bool IsPageSettings => _page == 3;

	public bool IsPageRank => _page == 4;

	public ViewMode CurrentViewMode => _viewMode;

	public bool IsViewDaily => _viewMode == ViewMode.Daily;

	public bool IsViewWeekly => _viewMode == ViewMode.Weekly;

	public bool IsViewMonthly => _viewMode == ViewMode.Monthly;

	public bool IsViewYearly => _viewMode == ViewMode.Yearly;

	public bool IsViewPeriod => _viewMode != ViewMode.Daily;

	public string BoardDateTitle
	{
		get
		{
			DateTime value = ((_aggregate.Start == default(DateTime)) ? _boardDate : _aggregate.Start);
			DateTime value2 = ((_aggregate.End == default(DateTime)) ? _boardDate : _aggregate.End);
			return _viewMode switch
			{
				ViewMode.Daily => $"{value:yyyy} | {value:MM.dd}", 
				ViewMode.Weekly => (value.Year == value2.Year) ? $"{value:yyyy} | {value:MM.dd}-{value2:MM.dd}" : $"{value:yyyy.MM.dd}-{value2:yyyy.MM.dd}", 
				ViewMode.Monthly => $"{value:yyyy} | {value.Month}月", 
				ViewMode.Yearly => $"{value:yyyy} | 年度", 
				_ => $"{value:yyyy} | {value:MM.dd}", 
			};
		}
	}

	public string BoardSubtitle => _aggregate.Title + " · " + _aggregate.RangeText;

	public string BoardUpdatedText => "统计截至 " + _boardUpdatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

	public string CalendarTitle => _boardDate.ToString("yyyy年M月", CultureInfo.GetCultureInfo("zh-CN"));

	public string BoardYearLabel => _boardDate.Year + "年";

	public string CalendarToggleGlyph
	{
		get
		{
			if (!_calendarExpanded)
			{
				return "⌄";
			}
			return "⌃";
		}
	}

	public bool IsCalendarExpanded => _calendarExpanded;

	public bool IsDetailOpen => _detailOpen;

	public bool IsAnalysisExpanded => _analysisExpanded;

	public bool ShowCalendarLegend => Config.ShowCalendarLegend;

	public string DetailPeriodText => _aggregate.RangeText;

	public bool HasDetailRecords => DetailRecords.Count > 0;

	public string DayShiftText
	{
		get
		{
			DailyRecord recordForDate = GetRecordForDate(_boardDate);
			string value;
			if (Config.IsWorkday(_boardDate))
			{
				if (recordForDate == null)
				{
					value = "无记录";
				}
				else if (recordForDate.HolidayOvertime)
				{
					value = "节假日加班";
				}
				else
				{
					value = (recordForDate.IsOffDuty ? "已下班" : "正常搬砖");
				}
			}
			else
			{
				value = "节假日停计";
			}
			return $"当日班次：标准排班 {Config.WorkStart}-{Config.WorkEnd}（午休 {Config.LunchStart}-{Config.LunchEnd}） | 状态：{value}";
		}
	}

	public string DaySummaryText
	{
		get
		{
			DailyRecord? recordForDate = GetRecordForDate(_boardDate);
			double num = recordForDate?.WorkedSeconds ?? 0.0;
			decimal value = recordForDate?.SlackingEarned ?? 0m;
			decimal value2 = recordForDate?.OvertimeLoss ?? 0m;
			return $"出勤与对冲统计：有效搬砖 {num / 3600.0:F1} 小时，摸鱼白赚 ¥{Money(value, 2)}，义务加班血亏 ¥{Money(value2, 2)}";
		}
	}

	public ObservableCollection<BoardMetric> BoardMetrics { get; } = new ObservableCollection<BoardMetric>();

	public ObservableCollection<BoardCalendarDay> CalendarDays { get; } = new ObservableCollection<BoardCalendarDay>();

	public ObservableCollection<BoardChip> WeekChips { get; } = new ObservableCollection<BoardChip>();

	public ObservableCollection<BoardChip> MonthChips { get; } = new ObservableCollection<BoardChip>();

	public ObservableCollection<BoardChip> YearChips { get; } = new ObservableCollection<BoardChip>();

	public ObservableCollection<DailyRecord> DetailRecords { get; } = new ObservableCollection<DailyRecord>();

	public DateTime BoardDate => _boardDate;

	public string BoardTitle => _aggregate.Title;

	public string BoardRangeText => _aggregate.RangeText;

	public string BoardIncomeText => "¥" + Money(_aggregate.TotalIncome, 2);

	public string BoardSlackingText => "¥" + Money(_aggregate.SlackingTotal, 2);

	public string BoardOvertimeText => "- ¥" + Money(_aggregate.OvertimeTotal, 2);

	public string BoardRoundingText => "- ¥" + Money(_aggregate.RoundingTotal, 2);

	public decimal BoardHedgeNet => _aggregate.HedgeNet;

	public string BoardHeadline
	{
		get
		{
			if (_aggregate.ActiveDayCount == 0 && _aggregate.TotalIncome <= 0m)
			{
				return "这个周期还没有账，先去搬砖吧";
			}
			decimal hedgeNet = _aggregate.HedgeNet;
			if (hedgeNet > 0m)
			{
				return "\ud83c\udfc6 反薅成功！净赚老板 ¥" + Money(hedgeNet, 2);
			}
			if (hedgeNet < 0m)
			{
				return "\ud83d\ude2d 纯纯倒贴！累计倒贴老板 ¥" + Money(-hedgeNet, 2);
			}
			return "\ud83d\ude10 暂时打平，谁也没薅到谁";
		}
	}

	public bool IsNetPositive => _aggregate.HedgeNet > 0m;

	public bool IsNetNegative => _aggregate.HedgeNet < 0m;

	public string BoardFormulaText => "净白嫖 = 摸鱼白嫖 " + Money(_aggregate.SlackingTotal, 2) + " − (加班血亏 " + Money(_aggregate.OvertimeTotal, 2) + " + 抹零损失 " + Money(_aggregate.RoundingTotal, 2) + ")";

	public string BoardPaidTimeText => Duration(_aggregate.TotalPaidSeconds);

	public string BoardOvertimeTimeText => Duration(_aggregate.TotalOvertimeSeconds);

	public string BoardSlackingTimeText => Duration(_aggregate.TotalSlackingSeconds);

	public string BoardTotalTimeText => Duration(_aggregate.TotalWorkedSeconds);

	public string BoardAvgTimeText
	{
		get
		{
			if (_aggregate.ActiveDayCount > 0)
			{
				return Duration(_aggregate.TotalWorkedSeconds / (double)_aggregate.ActiveDayCount);
			}
			return "—";
		}
	}

	public string BoardDaysText
	{
		get
		{
			if (_aggregate.ActiveDayCount > 0)
			{
				return $"{_aggregate.ActiveDayCount} 天有工时 · 日均工时 {BoardAvgTimeText} · 日均收入 ¥" + Money(_aggregate.TotalIncome / (decimal)_aggregate.ActiveDayCount, 2);
			}
			return "暂无工时记录";
		}
	}

	public bool ShowBoardAlert => _aggregate.Alert;

	public string BoardAlertText => "\ud83d\udea8 熔断警告：本周期老板白嫖金额已突破人类忍耐极限，建议立即启动简历更新程序！";

	public string BoardAlertLimitText => "义务加班血亏 ¥" + Money(_aggregate.OvertimeTotal, 2) + " / 忍耐上限 ¥" + Money(_aggregate.ToleranceLimit, 2);

	public ObservableCollection<SlackingSlice> Slices { get; } = new ObservableCollection<SlackingSlice>();

	public bool HasSlices => Slices.Count > 0;

	public string SlicesEmptyText => "这个周期还没摸过鱼——不是不想，是没来得及。";

	public ObservableCollection<DailyRecord> History { get; } = new ObservableCollection<DailyRecord>();

	public bool HasHistory => History.Count > 0;

	public OptionItem SelectedSalaryMode
	{
		get
		{
			return _selectedSalaryMode;
		}
		set
		{
			if (value != null && value != _selectedSalaryMode)
			{
				_selectedSalaryMode = value;
				Config.SalaryMode = (SalaryMode)value.Value;
				OnPropertyChanged("SelectedSalaryMode");
				UpdateSettingsPreview();
				RaiseAll();
			}
		}
	}

	public OptionItem SelectedRest
	{
		get
		{
			return _selectedRest;
		}
		set
		{
			if (value == null || value == _selectedRest)
			{
				return;
			}
			OptionItem selectedRest = _selectedRest;
			RestSystem restSystem = Config.RestSystem;
			RestSystem restSystem2 = (RestSystem)value.Value;
			_selectedRest = value;
			Config.RestSystem = restSystem2;
			if (restSystem2 == RestSystem.BigSmallWeek)
			{
				DateTime dateTime = UserConfig.WeekMonday(_sessionDate);
				Config.HolidayCalendar.BigSmallWeekAnchorMonday = dateTime;
				Func<DateTime, bool?> func = BigSmallWeekPromptRequested;
				bool? flag = ((func == null) ? new bool?(true) : func(dateTime));
				if (!flag.HasValue)
				{
					_selectedRest = selectedRest;
					Config.RestSystem = restSystem;
					OnPropertyChanged("SelectedRest");
					return;
				}
				Config.HolidayCalendar.BigSmallWeekAnchorIsBig = flag.Value;
			}
			Config.HolidayCalendar.RegularRestDays.Clear();
			_storage.SaveConfig(Config);
			OnPropertyChanged("SelectedRest");
			UpdateSettingsPreview();
			BuildHolidayDays();
			RefreshBoard();
			RaiseAll();
		}
	}

	public OptionItem SelectedBossMode
	{
		get
		{
			return _selectedBoss;
		}
		set
		{
			if (value != null && value != _selectedBoss)
			{
				_selectedBoss = value;
				Config.BossKeyMode = (BossKeyMode)value.Value;
				_storage.SaveConfig(Config);
				OnPropertyChanged("SelectedBossMode");
				SetCamouflage(Config.BossKeyMode != BossKeyMode.Stealth);
			}
		}
	}

	public bool StartWithWindows
	{
		get
		{
			return Config.StartWithWindows;
		}
		set
		{
			if (value != Config.StartWithWindows)
			{
				if (!StartupService.TrySet(value, out string error) && (MessageBox.Show("当前权限无法写入开机启动项：\n" + error + "\n\n是否以管理员权限重试？\n（若仍失败，关闭弹窗后点「生成启动项文件」手动启用）", "薪动 · 开机自启动", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes || !StartupService.TrySetElevated(value, out error)))
				{
					SettingsMessage = "❌ 开机自启动设置失败：" + error;
					OnPropertyChanged("StartWithWindows");
				}
				else
				{
					Config.StartWithWindows = value;
					MainViewModel mainViewModel = this;
					string settingsMessage;
					if (_storage.SaveConfig(Config))
					{
						settingsMessage = (value ? "✅ 已开启开机自启动（仅当前 Windows 用户）" : "✅ 已关闭开机自启动");
					}
					else
					{
						settingsMessage = "⚠\ufe0f 启动项已更新，但配置保存失败：" + _storage.LastError;
					}
					mainViewModel.SettingsMessage = settingsMessage;
					OnPropertyChanged("StartWithWindows");
				}
			}
		}
	}

	public string HolidayYearLabel => _holidayYear + "年";

	public string HolidayMonthLabel => _holidayMonth + "月";

	public ObservableCollection<HolidayCalendarDay> HolidayDays { get; } = new ObservableCollection<HolidayCalendarDay>();

	public string HolidaySummaryText
	{
		get
		{
			int num = DateTime.DaysInMonth(_holidayYear, _holidayMonth);
			int num2 = 0;
			for (int i = 1; i <= num; i++)
			{
				if (Config.IsWorkday(new DateTime(_holidayYear, _holidayMonth, i)))
				{
					num2++;
				}
			}
			return $"本月需上班 {num2} 天 | 本月休假 {num - num2} 天";
		}
	}

	public string HolidayQuotaText
	{
		get
		{
			int monthlyRestQuota = Config.MonthlyRestQuota;
			if (monthlyRestQuota <= 0)
			{
				return string.Empty;
			}
			int num = SelectedMonthlyRestCount();
			int num2 = Math.Max(0, monthlyRestQuota - num);
			if (num2 > 0)
			{
				return $"月休名额：已选 {num}/{monthlyRestQuota} 天，还需选 {num2} 天";
			}
			return $"月休名额：已选 {num}/{monthlyRestQuota} 天";
		}
	}

	public string ScheduleSummaryText
	{
		get
		{
			if (Config.RestSystem == RestSystem.BigSmallWeek)
			{
				if (!Config.IsBigWeek(_sessionDate))
				{
					return "大小休：本周小周（仅周日休 1 天），之后自动交替";
				}
				return "大小休：本周大周（周六日休 2 天），之后自动交替";
			}
			return Config.RestSystem switch
			{
				RestSystem.NoRest => "无休：每天按工作日计，节假日/补班仍生效", 
				RestSystem.DoubleWeekend => "双休：周六、周日休息", 
				RestSystem.Rest2Days => $"月休 2 天：本月已选 {SelectedMonthlyRestCount()}/2 天", 
				RestSystem.Rest4Days => $"月休 4 天：本月已选 {SelectedMonthlyRestCount()}/4 天", 
				_ => "按休息制度自动排班", 
			};
		}
	}

	public string HolidaySyncMessage => _holidaySyncMessage;

	public Brush HolidaySyncBrush => _holidaySyncBrush;

	public bool HasHolidaySyncMessage => !string.IsNullOrWhiteSpace(_holidaySyncMessage);

	public bool IsHolidaySyncing => _isHolidaySyncing;

	public bool IsBigSmallWeek => Config.RestSystem == RestSystem.BigSmallWeek;

	public string HolidaySyncButtonText
	{
		get
		{
			if (!_isHolidaySyncing)
			{
				return "\ud83c\udde8\ud83c\uddf3 一键同步";
			}
			return "⏳ 同步中…";
		}
	}

	public string StandardHoursDisplay => Config.ComputedStandardHours.ToString("0.##", CultureInfo.InvariantCulture) + " 小时";

	public string DailyWageText => "¥" + Money(Config.DailySalary, 2);

	public string HourlyWageText => "¥" + Money(Config.HourlySalary, 2);

	public string MinuteWageText => "¥" + Money(Config.MinuteSalary, 4);

	public string SecondWageText => "¥" + Money(Config.SecondSalary, 4);

	public string OvertimeBaseHourlyDisplay
	{
		get
		{
			if (!Config.UseCustomOvertimeBase)
			{
				return "加班基数时薪 ¥" + Money(Config.OvertimeBaseHourly, 4) + "（按全额月薪 / 实际时薪）";
			}
			return $"加班基数时薪 ¥{Money(Config.OvertimeBaseHourly, 4)}（底薪 ¥{Money(Config.OvertimeBaseSalary, 2)}/月）";
		}
	}

	public string SettingsPreviewText => $"折算月计薪天数 D = {Config.PayDays:0.##}\u3000每日标准工时 H = {Config.ComputedStandardHours:0.##} 小时\n每日生存回本线 ¥{Config.DailySurvivalLine:F2}\u3000当前模式：{Config.SalaryMode.DisplayName()}";

	public string SettingsMessage
	{
		get
		{
			return _settingsMessage;
		}
		private set
		{
			_settingsMessage = value;
			OnPropertyChanged("SettingsMessage");
			OnPropertyChanged("HasSettingsMessage");
		}
	}

	public bool HasSettingsMessage => !string.IsNullOrWhiteSpace(_settingsMessage);

	public string HotkeyStatusText => _hotkeyStatusText;

	public bool WorkMon
	{
		get
		{
			return HasWorkDay(DayOfWeek.Monday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Monday, value);
		}
	}

	public bool WorkTue
	{
		get
		{
			return HasWorkDay(DayOfWeek.Tuesday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Tuesday, value);
		}
	}

	public bool WorkWed
	{
		get
		{
			return HasWorkDay(DayOfWeek.Wednesday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Wednesday, value);
		}
	}

	public bool WorkThu
	{
		get
		{
			return HasWorkDay(DayOfWeek.Thursday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Thursday, value);
		}
	}

	public bool WorkFri
	{
		get
		{
			return HasWorkDay(DayOfWeek.Friday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Friday, value);
		}
	}

	public bool WorkSat
	{
		get
		{
			return HasWorkDay(DayOfWeek.Saturday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Saturday, value);
		}
	}

	public bool WorkSun
	{
		get
		{
			return HasWorkDay(DayOfWeek.Sunday);
		}
		set
		{
			SetWorkDay(DayOfWeek.Sunday, value);
		}
	}

	public ICommand StartSlackCommand { get; }

	public ICommand StopSlackCommand { get; }

	public ICommand ClockOutCommand { get; }

	public ICommand ResumeWorkCommand { get; }

	public ICommand UndoOffDutyCommand { get; }

	public ICommand TriggerEmergencyCallCommand { get; }

	public ICommand EndEmergencyCallCommand { get; }

	public ICommand ToggleTodayOffCommand { get; }

	public ICommand HolidayOvertimeCommand { get; }

	public ICommand SetPageCommand { get; }

	public ICommand SetViewCommand { get; }

	public ICommand SaveSettingsCommand { get; }

	public ICommand SaveSalaryOnlyCommand { get; }

	public ICommand ToggleBossKeyCommand { get; }

	public ICommand MinimizeToIslandCommand { get; }

	public ICommand SelectDayCommand { get; }

	public ICommand ToggleCalendarCommand { get; }

	public ICommand PrevWeekCommand { get; }

	public ICommand NextWeekCommand { get; }

	public ICommand PrevCalendarMonthCommand { get; }

	public ICommand NextCalendarMonthCommand { get; }

	public ICommand SelectWeekCommand { get; }

	public ICommand SelectMonthCommand { get; }

	public ICommand SelectYearCommand { get; }

	public ICommand PrevYearCommand { get; }

	public ICommand NextYearCommand { get; }

	public ICommand ToggleDetailCommand { get; }

	public ICommand CloseDetailCommand { get; }

	public ICommand ToggleAnalysisCommand { get; }

	public ICommand CloseLegendCommand { get; }

	public ICommand ShowLegendCommand { get; }

	public ICommand PrevHolidayMonthCommand { get; }

	public ICommand NextHolidayMonthCommand { get; }

	public ICommand PrevHolidayYearCommand { get; }

	public ICommand NextHolidayYearCommand { get; }

	public ICommand ToggleHolidayDayCommand { get; }

	public ICommand SyncHolidaysCommand { get; }

	public ICommand ResetHolidaysCommand { get; }

	public ICommand ChangeBigSmallWeekCommand { get; }

	public bool IsCamouflage => _camouflage;

	public string CamouflageText
	{
		get
		{
			decimal todayEarned = TodayEarned;
			decimal slackingAmount = SlackingAmount;
			decimal liveOvertimeLoss = LiveOvertimeLoss;
			bool flag = DisplayOvertimeSeconds > 0.5;
			string text = Duration(_today.WorkedSeconds + DisplayOvertimeSeconds);
			return Config.BossKeyMode switch
			{
				BossKeyMode.PlcCamouflage => $"[RUN] Port:COM7 | Mem: {todayEarned:F2} MB | Cache: {slackingAmount:F2} MB | Err: {liveOvertimeLoss:F2}" + (flag ? (" | Run: " + text) : string.Empty), 
				BossKeyMode.ExcelCamouflage => $"就绪 | 平均值: {todayEarned:F4} | 计数: {slackingAmount:F2}K | 求和: {liveOvertimeLoss:F4}" + (flag ? (" | 耗时: " + text) : string.Empty), 
				_ => "[RUN] System Host Service: Normal", 
			};
		}
	}

	private string TodayKey => _sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	public event EventHandler? BossKeyRequested;

	public event EventHandler? MinimizeToIslandRequested;

	public event Action<string, string>? AlertRequested;

	public event Func<DateTime, bool?>? BigSmallWeekPromptRequested;

	public event Action<string>? IslandNoticeRequested;

	public event Func<RoundingWarningInfo, bool>? RoundingWarningRequested;

	public event Func<EmergencyCallOptions?>? EmergencyCallOptionsRequested;

	public event Func<ClockOutTimeContext, DateTime?>? ClockOutTimeRequested;

	/// <summary>关键数据变更后请求上报后端（App 层接 ReportScheduler.ReportNowAsync）。</summary>
	public event Action? ReportNeeded;

	/// <summary>排行页 ViewModel，由 App 在启用加入排行时注入；未加入时为 null。</summary>
	public RankViewModel? Rank
	{
		get => _rank;
		set { _rank = value; OnPropertyChanged(); }
	}
	private RankViewModel? _rank;

	public event PropertyChangedEventHandler? PropertyChanged;

	private static Brush MakeBrush(string hex)
	{
		SolidColorBrush solidColorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
		((Freezable)solidColorBrush).Freeze();
		return solidColorBrush;
	}

	private static Brush MakeSoftBrush(string hex)
	{
		Color color = (Color)ColorConverter.ConvertFromString(hex);
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
		((Freezable)solidColorBrush).Freeze();
		return solidColorBrush;
	}

	public MainViewModel(StorageService storage, UserConfig config, Ledger ledger)
	{
		//IL_096c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0971: Unknown result type (might be due to invalid IL or missing references)
		//IL_098a: Expected Obj, but got Unknown
		_storage = storage;
		Config = config;
		_ledger = ledger;
		_sessionDate = BusinessDate(DateTime.Now);
		_hadHistory = _ledger.Records.Count > 0;
		if (!Config.FirstRunDate.HasValue)
		{
			DateTime dateTime = (from r in _ledger.Records
				select (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)) ? ((DateTime?)null) : new DateTime?(result) into d
				where d.HasValue
				select d.Value).DefaultIfEmpty(_sessionDate.Date).Min();
			Config.FirstRunDate = dateTime.Date;
			_storage.SaveConfig(Config);
		}
		_boardDate = _sessionDate;
		_today = GetOrCreateRecord(_sessionDate);
		FinalizeStaleEmergencyCalls();
		foreach (DailyRecord record in _ledger.Records)
		{
			EnsureSlackingSessions(record);
			EnsureEmergencySessions(record);
		}
		_offDuty = _today.IsOffDuty;
		_wallClockExcludedSeconds = Math.Max(0.0, _today.WallClockExcludedSeconds);
		_slackType = _today.ActiveSlackingType;
		_slacking = !_offDuty && _today.SlackingStartedAt.HasValue;
		DateTime? holidayOvertimeStart = _today.HolidayOvertimeStart;
		if (holidayOvertimeStart.HasValue)
		{
			DateTime valueOrDefault = holidayOvertimeStart.GetValueOrDefault();
			_holidayStart = valueOrDefault;
		}
		_holidayOvertime = !_offDuty && _today.HolidayOvertimeStart.HasValue;
		if (_today.EmergencyCallActive)
		{
			holidayOvertimeStart = _today.EmergencyCallStartedAt;
			if (holidayOvertimeStart.HasValue)
			{
				DateTime valueOrDefault2 = holidayOvertimeStart.GetValueOrDefault();
				if (BusinessDate(valueOrDefault2) == _sessionDate)
				{
					_emergency = true;
					_emergencyStart = valueOrDefault2;
					_emergencyOptions = new EmergencyCallOptions
					{
						Mode = _today.EmergencyCallMode,
						FixedSubsidy = _today.EmergencyCallFixedSubsidy,
						HourlyRate = _today.EmergencyCallHourlyRate
					};
					_emergencyRate = ((_today.EmergencyCallRate > 0m) ? _today.EmergencyCallRate : ((!IsWorkday(_sessionDate)) ? 3.0m : 2.0m));
					_emergencySeconds = Math.Max(0.0, (DateTime.Now - valueOrDefault2).TotalSeconds);
					_offDuty = true;
					_holidayOvertime = false;
					_state = WorkState.EmergencyOvertime;
				}
			}
		}
		AutoFillMissingDays(DateTime.Now);
		TryAutoCloseMissedCurrentDay(DateTime.Now);
		RestoreTodayState(DateTime.Now);
		_lastTick = DateTime.Now;
		_lastSave = DateTime.Now;
		_lastBoardRefresh = DateTime.MinValue;
		SalaryModeOptions = new List<OptionItem>
		{
			new OptionItem("月薪模式", SalaryMode.Monthly),
			new OptionItem("时薪模式", SalaryMode.Hourly)
		};
		RestOptions = (from r in Enum.GetValues<RestSystem>()
			select new OptionItem(r.DisplayName(), r)).ToList();
		BossOptions = (from m in Enum.GetValues<BossKeyMode>()
			select new OptionItem(m.DisplayName(), m)).ToList();
		_selectedSalaryMode = SalaryModeOptions.First((OptionItem o) => (SalaryMode)o.Value == Config.SalaryMode);
		_selectedRest = RestOptions.First((OptionItem o) => (RestSystem)o.Value == Config.RestSystem);
		_selectedBoss = BossOptions.First((OptionItem o) => (BossKeyMode)o.Value == Config.BossKeyMode);
		StartSlackCommand = new RelayCommand((object? p) =>
		{
			StartSlacking(ParseSlackType(p));
		}, (object? _) => CanStartSlack);
		StopSlackCommand = new RelayCommand((object? _) =>
		{
			StopSlacking();
		}, (object? _) => CanStopSlack);
		ClockOutCommand = new RelayCommand((object? _) =>
		{
			RequestClockOut();
		}, (object? _) => CanClockOut);
		ResumeWorkCommand = new RelayCommand((object? _) =>
		{
			UndoOffDuty();
		}, (object? _) => CanUndoOffDuty);
		UndoOffDutyCommand = new RelayCommand((object? _) =>
		{
			UndoOffDuty();
		}, (object? _) => CanUndoOffDuty);
		TriggerEmergencyCallCommand = new RelayCommand((object? _) =>
		{
			RequestEmergencyCall();
		}, (object? _) => CanTriggerEmergencyCall);
		EndEmergencyCallCommand = new RelayCommand((object? _) =>
		{
			EndEmergencyCall();
		}, (object? _) => CanEndEmergencyCall);
		ToggleTodayOffCommand = new RelayCommand((object? _) =>
		{
			ToggleTodayOff();
		});
		HolidayOvertimeCommand = new RelayCommand((object? _) =>
		{
			StartHolidayOvertime();
		}, (object? _) => CanHolidayOvertime);
		SetPageCommand = new RelayCommand((object? p) =>
		{
			SetPage(ParseInt(p));
		});
		SetViewCommand = new RelayCommand((object? p) =>
		{
			SetViewMode(p as string);
		});
		SaveSettingsCommand = new RelayCommand((object? _) =>
		{
			SaveSettings();
		});
		SaveSalaryOnlyCommand = new RelayCommand((object? _) =>
		{
			SaveSalaryOnly();
		});
		ToggleBossKeyCommand = new RelayCommand((object? _) =>
		{
			ToggleBossKey();
		});
		MinimizeToIslandCommand = new RelayCommand((object? _) =>
		{
			MinimizeToIslandRequested?.Invoke(this, EventArgs.Empty);
		});
		SelectDayCommand = new RelayCommand((object? p) =>
		{
			SelectBoardDay(p);
		});
		ToggleCalendarCommand = new RelayCommand((object? _) =>
		{
			ToggleCalendar();
		});
		PrevWeekCommand = new RelayCommand((object? _) =>
		{
			ShiftWeek(-1);
		});
		NextWeekCommand = new RelayCommand((object? _) =>
		{
			ShiftWeek(1);
		});
		PrevCalendarMonthCommand = new RelayCommand((object? _) =>
		{
			ShiftCalendarMonth(-1);
		});
		NextCalendarMonthCommand = new RelayCommand((object? _) =>
		{
			ShiftCalendarMonth(1);
		});
		SelectWeekCommand = new RelayCommand((object? p) =>
		{
			SelectWeekChip(p as BoardChip);
		});
		SelectMonthCommand = new RelayCommand((object? p) =>
		{
			SelectMonthChip(p as BoardChip);
		});
		SelectYearCommand = new RelayCommand((object? p) =>
		{
			SelectYearChip(p as BoardChip);
		});
		PrevYearCommand = new RelayCommand((object? _) =>
		{
			ShiftYear(-1);
		});
		NextYearCommand = new RelayCommand((object? _) =>
		{
			ShiftYear(1);
		});
		ToggleDetailCommand = new RelayCommand((object? _) =>
		{
			ToggleDetail();
		});
		CloseDetailCommand = new RelayCommand((object? _) =>
		{
			CloseDetail();
		});
		ToggleAnalysisCommand = new RelayCommand((object? _) =>
		{
			ToggleAnalysis();
		});
		CloseLegendCommand = new RelayCommand((object? _) =>
		{
			SetLegend(show: false);
		});
		ShowLegendCommand = new RelayCommand((object? _) =>
		{
			SetLegend(show: true);
		});
		PrevHolidayMonthCommand = new RelayCommand((object? _) =>
		{
			ShiftHolidayMonth(-1);
		});
		NextHolidayMonthCommand = new RelayCommand((object? _) =>
		{
			ShiftHolidayMonth(1);
		});
		PrevHolidayYearCommand = new RelayCommand((object? _) =>
		{
			ShiftHolidayYear(-1);
		});
		NextHolidayYearCommand = new RelayCommand((object? _) =>
		{
			ShiftHolidayYear(1);
		});
		ToggleHolidayDayCommand = new RelayCommand((object? p) =>
		{
			ToggleHolidayDay(p as HolidayCalendarDay);
		});
		SyncHolidaysCommand = new RelayCommand(async (object? _) =>
		{
			await SyncHolidaysAsync();
		}, (object? _) => !_isHolidaySyncing);
		ResetHolidaysCommand = new RelayCommand((object? _) =>
		{
			ResetHolidayYear();
		});
		ChangeBigSmallWeekCommand = new RelayCommand((object? _) =>
		{
			ChangeBigSmallWeek();
		});
		InitializeBoardMetrics();
		BuildHolidayDays();
		_timer = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromMilliseconds(200.0)
		};
		_timer.Tick += OnTick;
	}

	public void ApplySalarySetup(SalaryMode mode, decimal amount)
	{
		OptionItem optionItem = SalaryModeOptions.FirstOrDefault((OptionItem o) => (SalaryMode)o.Value == mode);
		if (optionItem != null && optionItem != _selectedSalaryMode)
		{
			_selectedSalaryMode = optionItem;
			OnPropertyChanged("SelectedSalaryMode");
		}
		Config.SalaryMode = mode;
		Config.SalaryAmount = amount;
		_storage.SaveConfig(Config);
		UpdateSettingsPreview();
		RefreshBoard();
		RaiseAll();
	}

	public void SetHotkeyStatus(string text)
	{
		_hotkeyStatusText = text;
		OnPropertyChanged("HotkeyStatusText");
	}

	public void Start()
	{
		_lastTick = DateTime.Now;
		_timer.Start();
		UpdateSettingsPreview();
		RefreshBoard();
		Persist();
		RaiseAll();
	}

	public void Stop()
	{
		_timer.Stop();
		Persist();
	}

	private void OnTick(object? sender, EventArgs e)
	{
		DateTime now = DateTime.Now;
		if (BusinessDate(now) != _sessionDate)
		{
			RollToNewDay(now);
		}
		double num = (now - _lastTick).TotalSeconds;
		_lastTick = now;
		if (double.IsNaN(num) || num < 0.0)
		{
			num = 0.0;
		}
		if (num > 30.0)
		{
			num = 30.0;
		}
		ReconcileTimeAxis(now);
		UpdateState(now, num);
		CheckReminders(now);
		if (IsOvertime && (now - _lastQuoteAt).TotalSeconds >= 8.0)
		{
			_lastQuoteAt = now;
			_overtimeQuoteIndex++;
			OnPropertyChanged("OvertimeQuoteText");
		}
		long num2 = now.Ticks / 10000000;
		if (num2 != _lastSecondKey || _forceRefresh)
		{
			_lastSecondKey = num2;
			_forceRefresh = false;
			RaiseAll();
		}
		if ((now - _lastSave).TotalSeconds >= 30.0)
		{
			Persist();
			if (_page == 2)
			{
				RefreshHistory();
			}
		}
		if (_page == 1 && (now - _lastBoardRefresh).TotalSeconds >= 2.0)
		{
			RefreshBoard();
		}
	}

	private void UpdateState(DateTime now, double delta)
	{
		bool flag = IsWorkday(_sessionDate);
		DateTime dateTime = At(_sessionDate, Config.WorkStart);
		DateTime dateTime2 = At(_sessionDate, Config.WorkEnd);
		if (dateTime2 <= dateTime)
		{
			dateTime2 = dateTime.AddHours(Math.Max(0.5, Config.ComputedStandardHours));
		}
		DateTime dateTime3 = At(_sessionDate, Config.LunchStart);
		DateTime dateTime4 = At(_sessionDate, Config.LunchEnd);
		bool flag2 = dateTime4 > dateTime3 && now >= dateTime3 && now < dateTime4;
		if (_emergency)
		{
			Transition(WorkState.EmergencyOvertime);
			UpdateEmergency(now);
		}
		else if (_offDuty)
		{
			Transition(WorkState.OffDuty);
		}
		else if (!flag)
		{
			Transition(WorkState.Holiday);
			if (_slacking)
			{
				StopSlackingInternal();
			}
			if (_holidayOvertime)
			{
				AccrueHolidayOvertime(now, delta);
			}
		}
		else if (now < dateTime)
		{
			Transition(WorkState.NotStarted);
		}
		else if (flag2)
		{
			if (_slacking)
			{
				StopSlackingInternal();
			}
			Transition(WorkState.LunchBreak);
		}
		else if (now >= dateTime2)
		{
			if (_slacking)
			{
				StopSlackingInternal();
			}
			Transition(WorkState.Overtime);
			AccrueOvertime(now, delta);
		}
		else if (_slacking)
		{
			Transition(WorkState.Slacking);
			AccrueSlacking(delta);
		}
		else
		{
			Transition(WorkState.Working);
			AccrueNormal(delta);
		}
	}

	private void Transition(WorkState next)
	{
		if (_state != next)
		{
			_state = next;
			_forceRefresh = true;
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private double AccrueNormal(double delta)
	{
		if (delta <= 0.0)
		{
			return 0.0;
		}
		double num = ((Config.SalaryMode == SalaryMode.Monthly) ? Math.Max(0.0, Config.ComputedStandardHours * 3600.0) : double.MaxValue);
		double val = Math.Max(0.0, num - _today.WorkedSeconds);
		double num2 = Math.Min(delta, val);
		if (num2 <= 0.0)
		{
			return 0.0;
		}
		_today.WorkedSeconds += num2;
		return num2;
	}

	private void AccrueSlacking(double delta)
	{
		double num = AccrueNormal(delta);
		if (!(num <= 0.0))
		{
			_today.SlackingSeconds += num;
			_slackSessionSeconds += num;
			SlackingSession activeSession = _today.SlackingSessions.LastOrDefault((SlackingSession s) => !s.End.HasValue);
			if (activeSession == null)
			{
				activeSession = new SlackingSession
				{
					Start = DateTime.Now.AddSeconds(-num),
					Type = _slackType
				};
				_today.SlackingSessions.Add(activeSession);
			}
			activeSession.Seconds += num;
			SlackingStat slackingStat = _today.SlackingStats.FirstOrDefault((SlackingStat s) => s.Type == _slackType);
			if (slackingStat == null)
			{
				slackingStat = new SlackingStat
				{
					Type = _slackType
				};
				_today.SlackingStats.Add(slackingStat);
			}
			slackingStat.Seconds += num;
			slackingStat.Amount = (decimal)slackingStat.Seconds * Config.SecondSalary;
		}
	}

	private void AccrueOvertime(DateTime now, double delta)
	{
		if (!(delta <= 0.0))
		{
			DateTime dateTime = At(_sessionDate, Config.WorkEnd);
			decimal num = ((Math.Max(0.0, (now - dateTime).TotalSeconds) <= Config.NormalOvertimeStageHours * 3600.0) ? Config.OvertimeRateNormal : Config.OvertimeRateNormalLate);
			decimal num2 = (decimal)delta * Config.OvertimeBaseHourly / 3600m * num;
			_today.OvertimeLoss += num2;
			_today.OvertimeSeconds += delta;
		}
	}

	private void AccrueHolidayOvertime(DateTime now, double delta)
	{
		if (!(delta <= 0.0))
		{
			decimal num = ((Math.Max(0.0, (now - _holidayStart).TotalSeconds) <= Config.HolidayOvertimeStageHours * 3600.0) ? Config.OvertimeRateHoliday : Config.OvertimeRateHolidayLate);
			decimal num2 = (decimal)delta * Config.OvertimeBaseHourly / 3600m * num;
			_today.OvertimeLoss += num2;
			_today.OvertimeSeconds += delta;
		}
	}

	private decimal LockedAmount(double seconds)
	{
		if (Config.SalaryMode != SalaryMode.Hourly)
		{
			return 0m;
		}
		return (decimal)Math.Floor(Math.Max(0.0, seconds) / 1800.0) * 0.5m * Config.HourlySalary;
	}

	private decimal PartialAmount(double seconds)
	{
		if (Config.SalaryMode != SalaryMode.Hourly)
		{
			return 0m;
		}
		return (decimal)(Math.Max(0.0, Math.Floor(seconds)) % 1800.0 / 1800.0) * 0.5m * Config.HourlySalary;
	}

	private decimal CurrentOvertimeRate()
	{
		DateTime now = DateTime.Now;
		if (_holidayOvertime)
		{
			if (!(Math.Max(0.0, (now - _holidayStart).TotalSeconds) <= Config.HolidayOvertimeStageHours * 3600.0))
			{
				return Config.OvertimeRateHolidayLate;
			}
			return Config.OvertimeRateHoliday;
		}
		DateTime dateTime = At(_sessionDate, Config.WorkEnd);
		if (!(Math.Max(0.0, (now - dateTime).TotalSeconds) <= Config.NormalOvertimeStageHours * 3600.0))
		{
			return Config.OvertimeRateNormalLate;
		}
		return Config.OvertimeRateNormal;
	}

	/// <summary>软件关闭跨过业务日切后，把昨日仍未结束的 Call 在日切点封账。</summary>
	private void FinalizeStaleEmergencyCalls()
	{
		foreach (DailyRecord record in _ledger.Records)
		{
			if (!record.EmergencyCallActive || !record.EmergencyCallStartedAt.HasValue)
			{
				continue;
			}
			DateTime startedAt = record.EmergencyCallStartedAt.Value;
			DateTime startDay = BusinessDate(startedAt).Date;
			if (startDay >= _sessionDate.Date)
			{
				continue;
			}
			DateTime cutoff = At(startDay, Config.WorkStart).AddDays(1.0).AddHours(-1.0);
			if (cutoff <= startedAt)
			{
				cutoff = startedAt.AddHours(1.0);
			}
			EnsureEmergencySessions(record);
			double seconds = Math.Max(0.0, (cutoff - startedAt).TotalSeconds);
			decimal fallbackRate = (IsWorkday(startDay) ? 2.0m : 3.0m);
			decimal rate = ((record.EmergencyCallRate > 0m) ? record.EmergencyCallRate : fallbackRate);
			decimal loss = ((record.EmergencyCallMode != EmergencyCallCompensationMode.Unpaid) ? 0m : ((decimal)seconds * Config.OvertimeBaseSecond * rate));
			decimal earned = record.EmergencyCallMode switch
			{
				EmergencyCallCompensationMode.FixedSubsidy => record.EmergencyCallFixedSubsidy,
				EmergencyCallCompensationMode.HourlyRate => (decimal)seconds * record.EmergencyCallHourlyRate / 3600m,
				_ => 0m
			};
			record.EmergencyCallSessions.Add(new EmergencyCallSession
			{
				Start = startedAt,
				End = cutoff,
				Mode = record.EmergencyCallMode,
				Seconds = seconds,
				Loss = loss,
				Earned = earned,
				Rate = ((record.EmergencyCallMode == EmergencyCallCompensationMode.Unpaid) ? rate : 0m),
				FixedSubsidy = record.EmergencyCallFixedSubsidy,
				HourlyRate = record.EmergencyCallHourlyRate
			});
			record.OvertimeSeconds += seconds;
			RebuildEmergencyAggregates(record);
			record.EmergencyCallActive = false;
			record.EmergencyCallEndedAt = cutoff;
			record.IsOffDuty = true;
		}
	}

	/// <summary>旧账本没有摸鱼区间时，用现有汇总补一条近似会话。</summary>
	private static void EnsureSlackingSessions(DailyRecord record)
	{
		record.SlackingSessions ??= new List<SlackingSession>();
		if (record.SlackingSessions.Count == 0 && record.SlackingSeconds > 0.01)
		{
			DateTime end = ((record.LastUpdated > DateTime.MinValue) ? record.LastUpdated : DateTime.Now);
			DateTime start = (record.SlackingStartedAt ?? end.AddSeconds(-record.SlackingSeconds));
			if (end <= start)
			{
				end = start.AddSeconds(record.SlackingSeconds);
			}
			record.SlackingSessions.Add(new SlackingSession
			{
				Start = start,
				End = end,
				Type = record.ActiveSlackingType,
				Seconds = record.SlackingSeconds
			});
		}
		NormalizeSlackingSessions(record);
	}

	private static void NormalizeSlackingSessions(DailyRecord record)
	{
		List<SlackingSession> sessions = record.SlackingSessions;
		if (sessions.Count <= 1)
		{
			record.SlackingCount = sessions.Count;
			return;
		}
		List<SlackingSession> merged = new List<SlackingSession>();
		foreach (SlackingSession session in sessions.OrderBy((SlackingSession s) => s.Start))
		{
			SlackingSession last = merged.LastOrDefault();
			bool sameBurst = last != null && last.Type == session.Type &&
				(last.End.HasValue && session.Start <= last.End.Value.AddSeconds(2.0));
			if (sameBurst && last != null)
			{
				last!.End = (session.End ?? last.End);
				last.Seconds += session.Seconds;
			}
			else
			{
				merged.Add(new SlackingSession
				{
					Start = session.Start,
					End = session.End,
					Type = session.Type,
					Seconds = session.Seconds
				});
			}
		}
		record.SlackingSessions = merged;
		record.SlackingCount = merged.Count;
		record.SlackingSeconds = merged.Sum((SlackingSession s) => s.Seconds);
	}

	/// <summary>旧账本没有 Call 会话列表时，用现有汇总补一条会话。</summary>
	private static void EnsureEmergencySessions(DailyRecord record)
	{
		record.EmergencyCallSessions ??= new List<EmergencyCallSession>();
		if (record.EmergencyCallSessions.Count > 0 || record.EmergencyCallSeconds <= 0.01)
		{
			return;
		}
		DateTime end = (record.EmergencyCallEndedAt ?? ((record.LastUpdated > DateTime.MinValue) ? record.LastUpdated : DateTime.Now));
		DateTime start = (record.EmergencyCallStartedAt ?? end.AddSeconds(-record.EmergencyCallSeconds));
		if (end <= start)
		{
			end = start.AddSeconds(record.EmergencyCallSeconds);
		}
		record.EmergencyCallSessions.Add(new EmergencyCallSession
		{
			Start = start,
			End = end,
			Mode = record.EmergencyCallMode,
			Seconds = record.EmergencyCallSeconds,
			Loss = record.EmergencyCallLoss,
			Earned = record.EmergencyCallEarned,
			Rate = record.EmergencyCallRate,
			FixedSubsidy = record.EmergencyCallFixedSubsidy,
			HourlyRate = record.EmergencyCallHourlyRate
		});
	}

	private void AutoFillMissingDays(DateTime now)
	{
		if (!_hadHistory)
		{
			return;
		}
		DateTime date = (Config.FirstRunDate ?? _sessionDate).Date;
		double num = Math.Max(0.5, Config.ComputedStandardHours) * 3600.0;
		for (int num2 = 31; num2 >= 1; num2--)
		{
			DateTime dateTime = _sessionDate.AddDays(-num2);
			if (!(dateTime.Date < date) && Config.IsWorkday(dateTime))
			{
				string key = dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				if (!_ledger.Records.Any((DailyRecord r) => r.Date == key))
				{
					decimal num3 = ((Config.SalaryMode == SalaryMode.Monthly) ? Config.DailySalary : LockedAmount(num));
					_ledger.Records.Add(new DailyRecord
					{
						Date = key,
						NormalEarned = num3,
						WorkedSeconds = num,
						IsOffDuty = true,
						ClockOutAt = At(dateTime, Config.WorkEnd),
						LockedHourlyAmount = ((Config.SalaryMode == SalaryMode.Hourly) ? num3 : 0m),
						AutoFilled = true,
						LastUpdated = now
					});
				}
			}
		}
	}

	private void TryAutoCloseMissedCurrentDay(DateTime now)
	{
		if (!_hadHistory || _offDuty || _emergency || _holidayOvertime || !IsWorkday(_sessionDate) || _today.ClockOutAt.HasValue || _today.EmergencyCallActive || _today.OvertimeSeconds > 0.5 || _today.OvertimeLoss > 0m)
		{
			return;
		}
		DateTime dateTime = At(_sessionDate, Config.WorkEnd);
		if (!(now < dateTime))
		{
			double num = CalculateWallClockWorkedSeconds(dateTime);
			_today.WorkedSeconds = num;
			_today.OvertimeSeconds = 0.0;
			_today.OvertimeLoss = 0m;
			_today.RoundingLoss = 0m;
			_today.RoundingLostSeconds = 0.0;
			_today.ClockOutAt = dateTime;
			_today.IsOffDuty = true;
			_today.AutoFilled = true;
			if (_today.SlackingStartedAt.HasValue)
			{
				DateTime slackStart = _today.SlackingStartedAt.Value;
				DateTime gapStart = ((slackStart > _today.LastUpdated) ? slackStart : _today.LastUpdated);
				if (gapStart < dateTime)
				{
					AddSlackingGap(gapStart, dateTime);
				}
				FinalizeActiveSlackingSession(dateTime);
				ClearSlackingSession();
			}
			_today.SlackingStartedAt = null;
			_today.SlackingSeconds = Math.Min(_today.SlackingSeconds, num);
			if (Config.SalaryMode == SalaryMode.Hourly)
			{
				decimal num2 = LockedAmount(num);
				_today.LockedHourlyAmount = num2;
				_today.NormalEarned = num2;
			}
			else
			{
				_today.NormalEarned = Config.DailySalary;
			}
			_offDuty = true;
			_slacking = false;
			_slackSessionSeconds = 0.0;
			_offAtNotified = true;
			_state = WorkState.OffDuty;
			_forceRefresh = true;
		}
	}

	private void RestoreTodayState(DateTime now)
	{
		if (_offDuty)
		{
			if (!_today.ClockOutAt.HasValue && _today.OvertimeSeconds > 0.0)
			{
				DateTime dateTime = At(_sessionDate, Config.WorkEnd);
				_today.ClockOutAt = dateTime.AddSeconds(_today.OvertimeSeconds);
			}
			ClearSlackingSession();
		}
		else if (!IsWorkday(_sessionDate))
		{
			_today.WorkedSeconds = 0.0;
			ClearSlackingSession();
			RecomputeOvertime(now);
		}
		else
		{
			_today.WorkedSeconds = CalculateWallClockWorkedSeconds(now);
			RecomputeOvertime(now);
			ResumeSlackingAfterRestart(now);
		}
	}

	private double CalculateWallClockWorkedSeconds(DateTime now)
	{
		DateTime dateTime = At(_sessionDate, Config.WorkStart);
		DateTime dateTime2 = At(_sessionDate, Config.WorkEnd);
		if (dateTime2 <= dateTime)
		{
			dateTime2 = dateTime.AddHours(Math.Max(0.5, Config.ComputedStandardHours));
		}
		DateTime dateTime3 = ((now < dateTime2) ? now : dateTime2);
		if (dateTime3 <= dateTime)
		{
			return 0.0;
		}
		double totalSeconds = (dateTime3 - dateTime).TotalSeconds;
		totalSeconds -= OverlapSeconds(dateTime, dateTime3, At(_sessionDate, Config.LunchStart), At(_sessionDate, Config.LunchEnd));
		totalSeconds -= Math.Max(0.0, _wallClockExcludedSeconds);
		totalSeconds = Math.Max(0.0, totalSeconds);
		if (Config.SalaryMode == SalaryMode.Monthly)
		{
			totalSeconds = Math.Min(totalSeconds, Math.Max(0.5, Config.ComputedStandardHours) * 3600.0);
		}
		return totalSeconds;
	}

	private static double OverlapSeconds(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
	{
		DateTime dateTime = ((aStart > bStart) ? aStart : bStart);
		DateTime dateTime2 = ((aEnd < bEnd) ? aEnd : bEnd);
		if (!(dateTime2 > dateTime))
		{
			return 0.0;
		}
		return (dateTime2 - dateTime).TotalSeconds;
	}

	private decimal OvertimeLossFor(double seconds, bool holiday)
	{
		if (seconds <= 0.0)
		{
			return 0m;
		}
		double num = Math.Max(0.01, holiday ? Config.HolidayOvertimeStageHours : Config.NormalOvertimeStageHours) * 3600.0;
		double num2 = Math.Min(seconds, num);
		double num3 = Math.Max(0.0, seconds - num);
		decimal num4 = (holiday ? Config.OvertimeRateHoliday : Config.OvertimeRateNormal);
		decimal num5 = (holiday ? Config.OvertimeRateHolidayLate : Config.OvertimeRateNormalLate);
		return ((decimal)num2 * num4 + (decimal)num3 * num5) * Config.OvertimeBaseHourly / 3600m;
	}

	private void RecomputeOvertime(DateTime now)
	{
		if (!IsWorkday(_sessionDate))
		{
			if (_holidayOvertime)
			{
				DateTime? holidayOvertimeStart = _today.HolidayOvertimeStart;
				if (holidayOvertimeStart.HasValue)
				{
					DateTime valueOrDefault = holidayOvertimeStart.GetValueOrDefault();
					double num = Math.Max(0.0, (now - valueOrDefault).TotalSeconds);
					_today.OvertimeSeconds = num;
					_today.OvertimeLoss = OvertimeLossFor(num, holiday: true);
				}
			}
		}
		else
		{
			DateTime dateTime = At(_sessionDate, Config.WorkEnd);
			if (now <= dateTime)
			{
				_today.OvertimeSeconds = 0.0;
				_today.OvertimeLoss = 0m;
			}
			else
			{
				double totalSeconds = (now - dateTime).TotalSeconds;
				_today.OvertimeSeconds = totalSeconds;
				_today.OvertimeLoss = OvertimeLossFor(totalSeconds, holiday: false);
			}
		}
	}

	private void ResumeSlackingAfterRestart(DateTime now)
	{
		DateTime? slackingStartedAt = _today.SlackingStartedAt;
		if (slackingStartedAt.HasValue)
		{
			DateTime valueOrDefault = slackingStartedAt.GetValueOrDefault();
			if (BusinessDate(valueOrDefault) != _sessionDate)
			{
				ClearSlackingSession();
				return;
			}
			DateTime dateTime = At(_sessionDate, Config.WorkStart);
			DateTime dateTime2 = At(_sessionDate, Config.WorkEnd);
			DateTime dateTime3 = At(_sessionDate, Config.LunchStart);
			DateTime dateTime4 = At(_sessionDate, Config.LunchEnd);
			if (now >= dateTime2)
			{
				DateTime gapStart2 = ((valueOrDefault > _today.LastUpdated) ? valueOrDefault : _today.LastUpdated);
				if (gapStart2 < dateTime2)
				{
					AddSlackingGap(gapStart2, dateTime2);
				}
				FinalizeActiveSlackingSession(dateTime2);
				ClearSlackingSession();
				return;
			}
			if (now >= dateTime3 && now < dateTime4)
			{
				DateTime gapStart3 = ((valueOrDefault > _today.LastUpdated) ? valueOrDefault : _today.LastUpdated);
				if (gapStart3 < dateTime3)
				{
					AddSlackingGap(gapStart3, dateTime3);
				}
				FinalizeActiveSlackingSession(dateTime3);
				ClearSlackingSession();
				return;
			}
			if (now >= dateTime4 && valueOrDefault < dateTime3)
			{
				DateTime gapStart4 = ((valueOrDefault > _today.LastUpdated) ? valueOrDefault : _today.LastUpdated);
				if (gapStart4 < dateTime3)
				{
					AddSlackingGap(gapStart4, dateTime3);
				}
				FinalizeActiveSlackingSession(dateTime3);
				ClearSlackingSession();
				return;
			}
			if (now < dateTime)
			{
				ClearSlackingSession();
				return;
			}
			DateTime dateTime5 = ((valueOrDefault > _today.LastUpdated) ? valueOrDefault : _today.LastUpdated);
			if (dateTime5 > now)
			{
				dateTime5 = now;
			}
			AddSlackingGap(dateTime5, now);
			_slacking = true;
			_slackType = _today.ActiveSlackingType;
			_slackSessionSeconds = Math.Max(0.0, (now - valueOrDefault).TotalSeconds);
		}
		else
		{
			_slacking = false;
		}
	}

	private void AddSlackingGap(DateTime from, DateTime to)
	{
		if (to <= from)
		{
			return;
		}
		double totalSeconds = (to - from).TotalSeconds;
		totalSeconds -= OverlapSeconds(from, to, At(to, Config.LunchStart), At(to, Config.LunchEnd));
		totalSeconds = Math.Max(0.0, totalSeconds);
		if (!(totalSeconds <= 0.0))
		{
			_today.SlackingSeconds += totalSeconds;
			SlackingSession activeGapSession = _today.SlackingSessions.LastOrDefault((SlackingSession s) => !s.End.HasValue);
			if (activeGapSession == null)
			{
				activeGapSession = new SlackingSession
				{
					Start = from,
					Type = _today.ActiveSlackingType
				};
				_today.SlackingSessions.Add(activeGapSession);
			}
			activeGapSession.Seconds += totalSeconds;
			SlackingStat slackingStat = _today.SlackingStats.FirstOrDefault((SlackingStat s) => s.Type == _today.ActiveSlackingType);
			if (slackingStat == null)
			{
				slackingStat = new SlackingStat
				{
					Type = _today.ActiveSlackingType
				};
				_today.SlackingStats.Add(slackingStat);
			}
			slackingStat.Seconds += totalSeconds;
			slackingStat.Amount = (decimal)slackingStat.Seconds * Config.SecondSalary;
		}
	}

	private void ClearSlackingSession()
	{
		_slacking = false;
		_slackSessionSeconds = 0.0;
		_today.SlackingStartedAt = null;
	}

	private void FinalizeActiveSlackingSession(DateTime end)
	{
		SlackingSession active = _today.SlackingSessions.LastOrDefault((SlackingSession s) => !s.End.HasValue);
		if (active != null && end > active.Start)
		{
			active.End = end;
		}
	}

	private void TrimSlackingSessions(DateTime clockOut)
	{
		if (_today.SlackingSessions.Count == 0)
		{
			return;
		}
		_today.SlackingSessions.RemoveAll((SlackingSession s) => s.Start >= clockOut);
		foreach (SlackingSession session in _today.SlackingSessions)
		{
			if (!session.End.HasValue || session.End.Value > clockOut)
			{
				session.End = clockOut;
			}
			if (session.End.Value <= session.Start)
			{
				session.Seconds = 0.0;
				continue;
			}
			double seconds = (session.End.Value - session.Start).TotalSeconds;
			seconds -= OverlapSeconds(session.Start, session.End.Value, At(_sessionDate, Config.LunchStart), At(_sessionDate, Config.LunchEnd));
			session.Seconds = Math.Max(0.0, seconds);
		}
		List<SlackingSession> alive = _today.SlackingSessions.Where((SlackingSession s) => s.Seconds > 0.01).ToList();
		_today.SlackingSeconds = alive.Sum((SlackingSession s) => s.Seconds);
		_today.SlackingCount = alive.Count;
		_today.SlackingStats = alive.GroupBy((SlackingSession s) => s.Type).Select((IGrouping<SlackingType, SlackingSession> g) => new SlackingStat
		{
			Type = g.Key,
			Seconds = g.Sum((SlackingSession s) => s.Seconds),
			Amount = (decimal)g.Sum((SlackingSession s) => s.Seconds) * Config.SecondSalary
		}).ToList();
		_today.SlackingEarned = (decimal)_today.SlackingSeconds * Config.SecondSalary;
	}

	public void StartSlacking(SlackingType type)
	{
		if (CanStartSlack)
		{
			DateTime now = DateTime.Now;
			_slacking = true;
			_slackType = type;
			_slackSessionSeconds = 0.0;
			_slackWarned = false;
			_today.ActiveSlackingType = type;
			_today.SlackingStartedAt = now;
			_today.SlackingCount++;
			_today.SlackingSessions ??= new List<SlackingSession>();
			_today.SlackingSessions.Add(new SlackingSession
			{
				Start = now,
				Type = type,
				Seconds = 0.0
			});
			Transition(WorkState.Slacking);
			CommandManager.InvalidateRequerySuggested();
			Persist();
			RaiseAll();
		}
	}

	public void StopSlacking()
	{
		if (_slacking)
		{
			StopSlackingInternal();
		}
	}

	private void StopSlackingInternal()
	{
		if (_slacking)
		{
			FinalizeActiveSlackingSession(DateTime.Now);
			ClearSlackingSession();
			if (_state == WorkState.Slacking)
			{
				Transition(WorkState.Working);
			}
			CommandManager.InvalidateRequerySuggested();
			Persist();
		}
	}

	public void RequestClockOut()
	{
		if (!CanClockOut)
		{
			return;
		}
		DateTime now = DateTime.Now;
		DateTime dateTime = (_holidayOvertime ? _holidayStart : At(_sessionDate, Config.WorkStart));
		DateTime dateTime2 = At(_sessionDate, Config.WorkEnd);
		ClockOutTimeContext arg = new ClockOutTimeContext
		{
			BusinessDate = _sessionDate,
			WorkStart = At(_sessionDate, Config.WorkStart),
			WorkEnd = dateTime2,
			Earliest = dateTime,
			Now = now,
			HolidayOvertime = _holidayOvertime
		};
		Func<ClockOutTimeContext, DateTime?> func = ClockOutTimeRequested;
		DateTime? dateTime3 = ((func == null) ? new DateTime?(now) : func(arg));
		if (!dateTime3.HasValue)
		{
			return;
		}
		DateTime valueOrDefault = dateTime3.GetValueOrDefault();
		if (valueOrDefault > now.AddSeconds(1.0))
		{
			SettingsMessage = "❌ 下班时间不能晚于现在。";
			return;
		}
		if (valueOrDefault < dateTime)
		{
			SettingsMessage = "❌ 下班时间不能早于上班 / 加班开始时间。";
			return;
		}
		if (_slacking)
		{
			DateTime? slackingStartedAt = _today.SlackingStartedAt;
			if (slackingStartedAt.HasValue)
			{
				DateTime valueOrDefault2 = slackingStartedAt.GetValueOrDefault();
				if (valueOrDefault < valueOrDefault2)
				{
					SettingsMessage = "❌ 当前摸鱼还没结束，不能选到摸鱼开始之前。";
					return;
				}
			}
		}
		double num = (IsWorkday(_sessionDate) ? CalculateWallClockWorkedSeconds(valueOrDefault) : 0.0);
		if (Config.SalaryMode == SalaryMode.Hourly && valueOrDefault <= dateTime2 && !_holidayOvertime && num > 0.0)
		{
			double num2 = num % 1800.0;
			double num3 = num2 / 60.0;
			if (num3 >= 20.0 && num3 < 30.0)
			{
				double num4 = 1800.0 - num2;
				decimal num5 = 0.5m * Config.HourlySalary;
				RoundingWarningInfo arg2 = new RoundingWarningInfo(num4, num5, "\ud83d\udea8 血亏警告！", $"再摸鱼苟住 {Clock(num4)} 即可凑满半点，多拿 ¥{num5:F2}！\n" + "现在下班将白白抹零，确定便宜老板吗？");
				Func<RoundingWarningInfo, bool> func2 = RoundingWarningRequested;
				if (func2 != null && !func2(arg2))
				{
					return;
				}
			}
		}
		PerformClockOutAt(valueOrDefault);
	}

	private void ReconcileTimeAxis(DateTime now)
	{
		if (_offDuty || _emergency)
		{
			return;
		}
		if (IsWorkday(_sessionDate))
		{
			double num = CalculateWallClockWorkedSeconds(now);
			if (num < 0.0)
			{
				num = 0.0;
			}
			_today.WorkedSeconds = num;
			DateTime dateTime = At(_sessionDate, Config.WorkEnd);
			double num2 = Math.Max(0.0, (now - dateTime).TotalSeconds);
			_today.OvertimeSeconds = num2;
			_today.OvertimeLoss = OvertimeLossFor(num2, holiday: false);
		}
		else if (_holidayOvertime)
		{
			DateTime? holidayOvertimeStart = _today.HolidayOvertimeStart;
			if (holidayOvertimeStart.HasValue)
			{
				DateTime valueOrDefault = holidayOvertimeStart.GetValueOrDefault();
				double num3 = Math.Max(0.0, (now - valueOrDefault).TotalSeconds);
				_today.OvertimeSeconds = num3;
				_today.OvertimeLoss = OvertimeLossFor(num3, holiday: true);
			}
		}
	}

	private void PerformClockOut()
	{
		PerformClockOutAt(DateTime.Now);
	}

	private void PerformClockOutAt(DateTime clockOutAt)
	{
		if (_offDuty)
		{
			return;
		}
		DateTime now = DateTime.Now;
		double num = (IsWorkday(_sessionDate) ? CalculateWallClockWorkedSeconds(clockOutAt) : 0.0);
		TrimSlackingSessions(clockOutAt);
		double num2 = Math.Max(0.0, _today.SlackingSeconds);
		if (num2 > num && num2 > 0.0)
		{
			double num3 = num / num2;
			foreach (SlackingStat slackingStat in _today.SlackingStats)
			{
				slackingStat.Seconds *= num3;
				slackingStat.Amount = (decimal)slackingStat.Seconds * Config.SecondSalary;
			}
		}
		_today.SlackingSeconds = Math.Min(num2, num);
		_today.SlackingEarned = (decimal)_today.SlackingSeconds * Config.SecondSalary;
		_today.SlackingStartedAt = null;
		_slacking = false;
		_slackSessionSeconds = 0.0;
		_today.WorkedSeconds = num;
		_today.ClockOutAt = clockOutAt;
		_today.IsOffDuty = true;
		_today.AutoFilled = false;
		_today.RoundingLoss = 0m;
		_today.RoundingLostSeconds = 0.0;
		_offDuty = true;
		_holidayOvertime = false;
		_offAtNotified = true;
		_lastTick = now;
		_wallClockExcludedSeconds = 0.0;
		_today.WallClockExcludedSeconds = 0.0;
		Transition(WorkState.OffDuty);
		Persist();
		RefreshBoard();
		RefreshHistory();
		CommandManager.InvalidateRequerySuggested();
		RaiseAll();
	}

	public void UndoOffDuty()
	{
		if (CanUndoOffDuty)
		{
			DateTime now = DateTime.Now;
			_offDuty = false;
			_today.IsOffDuty = false;
			_today.ClockOutAt = null;
			_today.AutoFilled = false;
			_today.RoundingLoss = 0m;
			_today.RoundingLostSeconds = 0.0;
			_today.LockedHourlyAmount = 0m;
			_today.SlackingStartedAt = null;
			_offAtNotified = true;
			_lastTick = now;
			double num = CalculateWallClockWorkedSeconds(now);
			_wallClockExcludedSeconds = Math.Max(0.0, num - _today.WorkedSeconds);
			_today.WallClockExcludedSeconds = _wallClockExcludedSeconds;
			UpdateState(now, 0.0);
			Persist();
			RefreshBoard();
			RefreshHistory();
			CommandManager.InvalidateRequerySuggested();
			RaiseAll();
		}
	}

	public void ResumeWork()
	{
		UndoOffDuty();
	}

	public void RequestEmergencyCall()
	{
		if (!CanTriggerEmergencyCall)
		{
			return;
		}
		Func<EmergencyCallOptions> func = EmergencyCallOptionsRequested;
		EmergencyCallOptions emergencyCallOptions = ((func == null) ? new EmergencyCallOptions() : func());
		if (emergencyCallOptions != null)
		{
			if (emergencyCallOptions.Mode == EmergencyCallCompensationMode.FixedSubsidy && emergencyCallOptions.FixedSubsidy <= 0m)
			{
				SettingsMessage = "❌ 固定补贴必须大于 0。";
			}
			else if (emergencyCallOptions.Mode == EmergencyCallCompensationMode.HourlyRate && emergencyCallOptions.HourlyRate <= 0m)
			{
				SettingsMessage = "❌ 加班时薪必须大于 0。";
			}
			else
			{
				StartEmergencyCall(emergencyCallOptions);
			}
		}
	}

	public void StartEmergencyCall(EmergencyCallOptions options)
	{
		if (CanTriggerEmergencyCall)
		{
			if (_slacking)
			{
				StopSlackingInternal();
			}
			DateTime now = DateTime.Now;
			_offDuty = true;
			_today.IsOffDuty = true;
			DailyRecord today = _today;
			DateTime? clockOutAt = today.ClockOutAt;
			DateTime valueOrDefault = clockOutAt.GetValueOrDefault();
			if (!clockOutAt.HasValue)
			{
				valueOrDefault = now;
				DateTime? clockOutAt2 = valueOrDefault;
				today.ClockOutAt = clockOutAt2;
			}
			_emergency = true;
			_emergencyStart = now;
			_emergencyOptions = options;
			_emergencyRate = ((!IsWorkday(_sessionDate)) ? 3.0m : 2.0m);
			_emergencySeconds = 0.0;
			_holidayOvertime = false;
			_today.EmergencyCallActive = true;
			_today.EmergencyCallStartedAt = now;
			_today.EmergencyCallEndedAt = null;
			EnsureEmergencySessions(_today);
			_today.EmergencyCallMode = options.Mode;
			_today.EmergencyCallRate = ((options.Mode == EmergencyCallCompensationMode.Unpaid) ? _emergencyRate : 0m);
			_today.EmergencyCallFixedSubsidy = options.FixedSubsidy;
			_today.EmergencyCallHourlyRate = options.HourlyRate;
			Transition(WorkState.EmergencyOvertime);
			Persist();
			RefreshBoard();
			CommandManager.InvalidateRequerySuggested();
			RaiseAll();
		}
	}

	public void EndEmergencyCall()
	{
		if (_emergency)
		{
			FinalizeEmergency(DateTime.Now);
			Persist();
			RefreshBoard();
			RefreshHistory();
			CommandManager.InvalidateRequerySuggested();
			RaiseAll();
		}
	}

	private void FinalizeEmergency(DateTime now)
	{
		if (_emergency)
		{
			double num = Math.Max(0.0, (now - _emergencyStart).TotalSeconds);
			bool flag = !IsWorkday(_sessionDate);
			decimal num2;
			if (_emergencyRate > 0m)
			{
				num2 = _emergencyRate;
			}
			else
			{
				num2 = (flag ? 3.0m : 2.0m);
			}
			decimal num3 = ((_emergencyOptions.Mode == EmergencyCallCompensationMode.Unpaid) ? ((decimal)num * Config.OvertimeBaseSecond * num2) : 0m);
			decimal num4 = _emergencyOptions.Mode switch
			{
				EmergencyCallCompensationMode.FixedSubsidy => _emergencyOptions.FixedSubsidy, 
				EmergencyCallCompensationMode.HourlyRate => (decimal)num * _emergencyOptions.HourlyRate / 3600m, 
				_ => 0m, 
			};
			_today.OvertimeSeconds += num;
			EnsureEmergencySessions(_today);
			_today.EmergencyCallSessions.Add(new EmergencyCallSession
			{
				Start = _emergencyStart,
				End = now,
				Mode = _emergencyOptions.Mode,
				Seconds = num,
				Loss = num3,
				Earned = num4,
				Rate = ((_emergencyOptions.Mode == EmergencyCallCompensationMode.Unpaid) ? num2 : 0m),
				FixedSubsidy = _emergencyOptions.FixedSubsidy,
				HourlyRate = _emergencyOptions.HourlyRate
			});
			RebuildEmergencyAggregates(_today);
			_today.EmergencyCallActive = false;
			_today.EmergencyCallEndedAt = now;
			_today.EmergencyCallRate = ((_emergencyOptions.Mode == EmergencyCallCompensationMode.Unpaid) ? num2 : 0m);
			_today.ClockOutAt = now;
			_today.IsOffDuty = true;
			_offDuty = true;
			_emergency = false;
			_emergencySeconds = 0.0;
			_emergencyOptions = new EmergencyCallOptions();
			_offAtNotified = true;
			Transition(WorkState.OffDuty);
		}
	}

	private void UpdateEmergency(DateTime now)
	{
		_emergencySeconds = Math.Max(0.0, (now - _emergencyStart).TotalSeconds);
	}

	private decimal EmergencyLossFromSessions(DailyRecord record, DateTime date)
	{
		decimal fallbackRate = (Config.IsWorkday(date) ? 2.0m : 3.0m);
		decimal loss = 0m;
		foreach (EmergencyCallSession session in record.EmergencyCallSessions)
		{
			if (session.Mode == EmergencyCallCompensationMode.Unpaid)
			{
				decimal rate = ((session.Rate > 0m) ? session.Rate : fallbackRate);
				loss += (decimal)Math.Max(0.0, session.Seconds) * Config.OvertimeBaseSecond * rate;
			}
		}
		return loss;
	}

	private static void RebuildEmergencyAggregates(DailyRecord record)
	{
		List<EmergencyCallSession> sessions = record.EmergencyCallSessions;
		record.EmergencyCallSeconds = sessions.Sum((EmergencyCallSession s) => Math.Max(0.0, s.Seconds));
		record.EmergencyCallLoss = sessions.Sum((EmergencyCallSession s) => s.Loss);
		record.EmergencyCallEarned = sessions.Sum((EmergencyCallSession s) => s.Earned);
		record.EmergencyCallCount = sessions.Count;
	}

	public void ToggleTodayOff()
	{
		SetTodayOff(!IsTodayOff);
	}

	public void SetTodayOff(bool off)
	{
		Config.TodayOff = off;
		Config.TodayOffDate = (off ? TodayKey : string.Empty);
		if (off && _slacking)
		{
			StopSlackingInternal();
		}
		_storage.SaveConfig(Config);
		_forceRefresh = true;
		CommandManager.InvalidateRequerySuggested();
		RaiseAll();
	}

	public void StartHolidayOvertime()
	{
		if (CanHolidayOvertime)
		{
			_holidayOvertime = true;
			_holidayStart = DateTime.Now;
			_today.HolidayOvertime = true;
			_today.HolidayOvertimeStart = _holidayStart;
			_forceRefresh = true;
			CommandManager.InvalidateRequerySuggested();
			Persist();
			RaiseAll();
		}
	}

	public void ToggleBossKey()
	{
		BossKeyRequested?.Invoke(this, EventArgs.Empty);
	}

	public void SetCamouflage(bool camouflage)
	{
		_camouflage = camouflage;
		RaiseAll();
	}

	private void CheckReminders(DateTime now)
	{
		if (_state == WorkState.Slacking && _slackSessionSeconds >= 900.0 && !_slackWarned)
		{
			_slackWarned = true;
			IslandNoticeRequested?.Invoke("⚠\ufe0f 单次摸鱼已超 15 分钟，小心腿麻及周围领导视线！");
		}
		if (IsWorkday(_sessionDate) && !_offDuty && !_emergency)
		{
			DateTime dateTime = At(_sessionDate, Config.WorkEnd);
			if (!_off5Notified && now >= dateTime.AddMinutes(-5.0) && now < dateTime)
			{
				_off5Notified = true;
				IslandNoticeRequested?.Invoke("\ud83d\udfe2 还有 5 分钟下班，准备打卡，别给老板白干！");
			}
			if (!_offAtNotified && now >= dateTime)
			{
				_offAtNotified = true;
				AlertRequested?.Invoke("⏰ 下班时间到！", "请立即打开手机完成下班打卡，拒绝免费义务加班！");
			}
		}
	}

	private void RollToNewDay(DateTime now)
	{
		bool num = _state == WorkState.Overtime || _emergency || _holidayOvertime;
		if (_emergency)
		{
			FinalizeEmergency(now);
		}
		_today.SlackingStartedAt = null;
		_today.ActiveSlackingType = _slackType;
		FinalizeDay();
		if (!_today.IsOffDuty)
		{
			_today.IsOffDuty = true;
			_today.ClockOutAt = now;
		}
		if (Config.SalaryMode == SalaryMode.Monthly || _offDuty)
		{
			_today.NormalEarned = TodayEarned;
		}
		_sessionDate = BusinessDate(now);
		_today = GetOrCreateRecord(_sessionDate);
		_offDuty = false;
		_slacking = false;
		_slackSessionSeconds = 0.0;
		_slackWarned = false;
		_holidayOvertime = false;
		_emergency = false;
		_emergencySeconds = 0.0;
		_emergencyOptions = new EmergencyCallOptions();
		_wallClockExcludedSeconds = 0.0;
		_off5Notified = false;
		_offAtNotified = false;
		_lastTick = now;
		_forceRefresh = true;
		_today.IsOffDuty = false;
		_today.IsHoliday = !IsWorkday(_sessionDate);
		Persist();
		RefreshBoard();
		RefreshHistory();
		RaiseAll();
		if (num)
		{
			AlertRequested?.Invoke("⚠\ufe0f 已通宵至新工作日前 1 小时", "已自动为您结算封存昨日账本！\n新的一天从待打卡开始，通宵的工时与亏损都已完整保留在昨天。");
		}
	}

	private void FinalizeDay()
	{
		if (Config.SalaryMode == SalaryMode.Hourly && !_today.IsOffDuty && !(_today.WorkedSeconds <= 0.0))
		{
			decimal num = LockedAmount(_today.WorkedSeconds);
			decimal num2 = num + PartialAmount(_today.WorkedSeconds);
			_today.RoundingLoss = Math.Max(0m, num2 - num);
			_today.RoundingLostSeconds = Math.Max(0.0, _today.WorkedSeconds % 1800.0);
			_today.NormalEarned = num;
		}
	}

	private DailyRecord GetOrCreateRecord(DateTime date)
	{
		string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		DailyRecord dailyRecord = _ledger.Records.FirstOrDefault((DailyRecord r) => r.Date == key);
		if (dailyRecord != null)
		{
			return dailyRecord;
		}
		dailyRecord = new DailyRecord
		{
			Date = key
		};
		_ledger.Records.Add(dailyRecord);
		return dailyRecord;
	}

	private void SyncToday()
	{
		_today.WorkedSeconds = Math.Max(0.0, _today.WorkedSeconds);
		_today.SlackingSeconds = Math.Clamp(_today.SlackingSeconds, 0.0, _today.WorkedSeconds);
		_today.SlackingEarned = (decimal)_today.SlackingSeconds * Config.SecondSalary;
		_today.LockedHourlyAmount = LockedAmount(_today.WorkedSeconds);
		_today.NormalEarned = TodayEarned;
		_today.IsHoliday = !IsWorkday(_sessionDate);
		_today.IsOffDuty = _offDuty;
		RecomputeSettledAmounts();
		_today.LastUpdated = DateTime.Now;
		foreach (SlackingStat slackingStat in _today.SlackingStats)
		{
			slackingStat.Amount = (decimal)slackingStat.Seconds * Config.SecondSalary;
		}
	}

	private void RecomputeSettledAmounts()
	{
		if (!_offDuty || _emergency)
		{
			return;
		}
		if (_today.ClockOutAt is DateTime clockOut && BusinessDate(clockOut) == _sessionDate)
		{
			bool holiday = !IsWorkday(_sessionDate);
			EnsureEmergencySessions(_today);
			_today.EmergencyCallSeconds = _today.EmergencyCallSessions.Sum((EmergencyCallSession s) => Math.Max(0.0, s.Seconds));
			_today.EmergencyCallLoss = EmergencyLossFromSessions(_today, _sessionDate);
			if (holiday && _today.HolidayOvertimeStart is DateTime holidayStart)
			{
				_today.OvertimeSeconds = Math.Max(0.0, (clockOut - holidayStart).TotalSeconds);
			}
			else if (holiday)
			{
				_today.OvertimeSeconds = 0.0;
			}
			else
			{
				DateTime end = At(_sessionDate, Config.WorkEnd);
				_today.OvertimeSeconds = Math.Max(0.0, (clockOut - end).TotalSeconds);
			}
			double regularSeconds = Math.Max(0.0, _today.OvertimeSeconds - _today.EmergencyCallSeconds);
			_today.OvertimeLoss = OvertimeLossFor(regularSeconds, holiday) + _today.EmergencyCallLoss;
		}
		if (Config.SalaryMode == SalaryMode.Hourly)
		{
			decimal locked = LockedAmount(_today.WorkedSeconds);
			decimal estimate = locked + PartialAmount(_today.WorkedSeconds);
			_today.RoundingLoss = Math.Max(0m, estimate - locked);
			_today.RoundingLostSeconds = Math.Max(0.0, _today.WorkedSeconds % 1800.0);
			_today.NormalEarned = TodayEarned;
			_today.LockedHourlyAmount = locked;
		}
	}

	public void Persist()
	{
		SyncToday();
		_storage.SaveLedger(_ledger);
		_lastSave = DateTime.Now;
	}

	public void SaveBeforeExit()
	{
		_timer.Stop();
		Persist();
		_storage.SaveConfig(Config);
	}

	public void ExportCsv(string path)
	{
		Persist();
		_storage.ExportCsv(path, _ledger.Records);
		SettingsMessage = "✅ 已导出：" + Path.GetFileName(path);
	}

	public void SetPage(int index)
	{
		_page = Math.Clamp(index, 0, 4);
		OnPropertyChanged("CurrentPageIndex");
		OnPropertyChanged("IsPageToday");
		OnPropertyChanged("IsPageBoard");
		OnPropertyChanged("IsPageLedger");
		OnPropertyChanged("IsPageSettings");
		OnPropertyChanged("IsPageRank");
		if (_page == 1)
		{
			RefreshBoard();
		}
		if (_page == 2)
		{
			RefreshHistory();
		}
		if (_page == 4)
		{
			_ = _rank?.RefreshAsync();
		}
	}

	public void SetViewMode(string? name)
	{
		if (Enum.TryParse<ViewMode>(name, ignoreCase: true, out var result))
		{
			ViewMode viewMode = _viewMode;
			_viewMode = result;
			if (result == ViewMode.Weekly && viewMode != ViewMode.Weekly)
			{
				_weekChipsMonth = new DateTime(_boardDate.Year, _boardDate.Month, 1);
			}
			if (viewMode == ViewMode.Weekly && (result == ViewMode.Monthly || result == ViewMode.Yearly) && _weekChipsMonth != default(DateTime))
			{
				_boardDate = ((result == ViewMode.Monthly) ? new DateTime(_weekChipsMonth.Year, _weekChipsMonth.Month, 1) : new DateTime(_weekChipsMonth.Year, 1, 1));
			}
			OnPropertyChanged("CurrentViewMode");
			OnPropertyChanged("IsViewDaily");
			OnPropertyChanged("IsViewWeekly");
			OnPropertyChanged("IsViewMonthly");
			OnPropertyChanged("IsViewYearly");
			OnPropertyChanged("IsViewPeriod");
			RefreshBoard();
		}
	}

	public void RefreshBoard()
	{
		SyncToday();
		if (_boardDate == default(DateTime))
		{
			_boardDate = _sessionDate;
		}
		_aggregate = _storage.BuildAggregate(_ledger, _viewMode, _boardDate, Config);
		if (_emergency && _sessionDate.Date >= _aggregate.Start.Date && _sessionDate.Date <= _aggregate.End.Date)
		{
			_aggregate.TotalOvertimeSeconds += _emergencySeconds;
			_aggregate.EmergencyCallSeconds += _emergencySeconds;
			_aggregate.EmergencyCallCount = Math.Max(1, _aggregate.EmergencyCallCount);
			if (_emergencyOptions.Mode == EmergencyCallCompensationMode.Unpaid)
			{
				_aggregate.EmergencyCallLoss += BossOwedEmergencyFee;
				_aggregate.OvertimeTotal += BossOwedEmergencyFee;
			}
			else if (_emergencyOptions.Mode == EmergencyCallCompensationMode.HourlyRate)
			{
				_aggregate.EmergencyCallEarned += EmergencyEarnedLive;
				_aggregate.TotalIncome += EmergencyEarnedLive;
			}
		}
		Slices.Clear();
		foreach (SlackingSlice slice in _aggregate.Slices)
		{
			Slices.Add(slice);
		}
		EnsureBoardChips();
		BuildCalendarDays();
		UpdateBoardMetrics();
		if (_detailOpen)
		{
			BuildDetailRecords();
		}
		_boardUpdatedAt = DateTime.Now;
		_lastBoardRefresh = DateTime.Now;
		OnPropertyChanged("BoardTitle");
		OnPropertyChanged("BoardDateTitle");
		OnPropertyChanged("BoardSubtitle");
		OnPropertyChanged("BoardRangeText");
		OnPropertyChanged("BoardUpdatedText");
		OnPropertyChanged("BoardIncomeText");
		OnPropertyChanged("BoardSlackingText");
		OnPropertyChanged("BoardOvertimeText");
		OnPropertyChanged("BoardRoundingText");
		OnPropertyChanged("BoardHedgeNet");
		OnPropertyChanged("BoardHeadline");
		OnPropertyChanged("IsNetPositive");
		OnPropertyChanged("IsNetNegative");
		OnPropertyChanged("BoardFormulaText");
		OnPropertyChanged("BoardDaysText");
		OnPropertyChanged("BoardPaidTimeText");
		OnPropertyChanged("BoardOvertimeTimeText");
		OnPropertyChanged("BoardSlackingTimeText");
		OnPropertyChanged("BoardTotalTimeText");
		OnPropertyChanged("BoardAvgTimeText");
		OnPropertyChanged("CalendarTitle");
		OnPropertyChanged("BoardYearLabel");
		OnPropertyChanged("CalendarToggleGlyph");
		OnPropertyChanged("IsCalendarExpanded");
		OnPropertyChanged("DayShiftText");
		OnPropertyChanged("DaySummaryText");
		OnPropertyChanged("DetailPeriodText");
		OnPropertyChanged("HasDetailRecords");
		OnPropertyChanged("IsViewPeriod");
		OnPropertyChanged("ShowBoardAlert");
		OnPropertyChanged("BoardAlertLimitText");
		OnPropertyChanged("HasSlices");
	}

	private void InitializeBoardMetrics()
	{
		BoardMetrics.Clear();
		BoardMetrics.Add(new BoardMetric("平均工时(h)"));
		BoardMetrics.Add(new BoardMetric("出勤天数"));
		BoardMetrics.Add(new BoardMetric("休息/节假日(天)"));
		BoardMetrics.Add(new BoardMetric("累计实得工资(¥)"));
		BoardMetrics.Add(new BoardMetric("已结清生存天数"));
		BoardMetrics.Add(new BoardMetric("纯利润净额(¥)"));
		BoardMetrics.Add(new BoardMetric("摸鱼白嫖金额(¥)"));
		BoardMetrics.Add(new BoardMetric("摸鱼总时长(h)"));
		BoardMetrics.Add(new BoardMetric("带薪拉屎/摸鱼次数"));
		BoardMetrics.Add(new BoardMetric("义务加班时长(h)"));
		BoardMetrics.Add(new BoardMetric("老板少给金额(¥)"));
		BoardMetrics.Add(new BoardMetric("时薪抹零白干(分)"));
	}

	private void UpdateBoardMetrics()
	{
		if (BoardMetrics.Count < 12)
		{
			InitializeBoardMetrics();
		}
		SetMetric(BoardMetrics[0], (decimal)_aggregate.AverageWorkHours, "F2", BrushInk);
		SetMetric(BoardMetrics[1], _aggregate.ActiveDayCount, "F0", BrushInk);
		SetMetric(BoardMetrics[2], _aggregate.RestDayCount, "F0", BrushInk);
		SetMetric(BoardMetrics[3], _aggregate.TotalIncome, "F2", BrushInk);
		SetMetric(BoardMetrics[4], _aggregate.SettledSurvivalDays, "F0", BrushInk);
		SetMetric(BoardMetrics[5], _aggregate.ProfitNet, "F2", BrushInk, negativeIsRed: true);
		SetMetric(BoardMetrics[6], _aggregate.SlackingTotal, "F2", BrushGreen);
		SetMetric(BoardMetrics[7], (decimal)(_aggregate.TotalSlackingSeconds / 3600.0), "F2", BrushGreen);
		SetMetric(BoardMetrics[8], _aggregate.SlackingCount, "F0", BrushGreen);
		SetMetric(BoardMetrics[9], (decimal)(_aggregate.TotalOvertimeSeconds / 3600.0), "F2", BrushRed);
		SetMetric(BoardMetrics[10], _aggregate.OvertimeTotal, "F2", BrushRed);
		SetMetric(BoardMetrics[11], (decimal)(_aggregate.RoundingLostSeconds / 60.0), "F0", BrushRed);
	}

	private static void SetMetric(BoardMetric metric, decimal value, string format, Brush positive, bool negativeIsRed = false)
	{
		bool flag = Math.Abs(value) <= 0.0001m;
		BoardMetric boardMetric = metric;
		string valueText;
		if (flag)
		{
			valueText = ((format == "F0") ? "0" : "0.00");
		}
		else
		{
			valueText = value.ToString(format, CultureInfo.InvariantCulture);
		}
		boardMetric.ValueText = valueText;
		BoardMetric boardMetric2 = metric;
		Brush valueBrush;
		if (flag)
		{
			valueBrush = BrushZero;
		}
		else
		{
			valueBrush = ((negativeIsRed && value < 0m) ? BrushRed : positive);
		}
		boardMetric2.ValueBrush = valueBrush;
		metric.LabelBrush = (flag ? BrushZero : BrushLabel);
	}

	private void EnsureBoardChips()
	{
		DateTime dateTime = ((_viewMode == ViewMode.Weekly && _weekChipsMonth != default(DateTime)) ? _weekChipsMonth : new DateTime(_boardDate.Year, _boardDate.Month, 1));
		if (_weekChipsMonth != dateTime || WeekChips.Count == 0)
		{
			RebuildWeekChips(dateTime);
			_weekChipsMonth = dateTime;
		}
		if (_monthChipsYear != _boardDate.Year)
		{
			RebuildMonthChips(_boardDate.Year);
			_monthChipsYear = _boardDate.Year;
		}
		if (_boardDate.Year < _yearPageStart || _boardDate.Year > _yearPageStart + 8)
		{
			_yearPageStart = _boardDate.Year - 4;
		}
		if (YearChips.Count == 0 || YearChips[0].Number != _yearPageStart)
		{
			RebuildYearChips();
		}
		UpdateBoardChipSelection();
	}

	private void RebuildWeekChips(DateTime monthStart)
	{
		WeekChips.Clear();
		DateTime dateTime = monthStart.AddMonths(1).AddDays(-1.0);
		DateTime dateTime2 = monthStart.AddDays(-((int)(monthStart.DayOfWeek + 6) % 7));
		int num = 1;
		DateTime dateTime3 = dateTime2;
		while (dateTime3 <= dateTime)
		{
			WeekChips.Add(new BoardChip
			{
				Label = "第" + ChineseNumber(num) + "周",
				Number = num,
				Start = dateTime3,
				End = dateTime3.AddDays(6.0)
			});
			num++;
			dateTime3 = dateTime3.AddDays(7.0);
		}
	}

	private void RebuildMonthChips(int year)
	{
		MonthChips.Clear();
		for (int i = 1; i <= 12; i++)
		{
			DateTime start = new DateTime(year, i, 1);
			MonthChips.Add(new BoardChip
			{
				Label = i + "月",
				Number = i,
				Start = start,
				End = start.AddMonths(1).AddDays(-1.0)
			});
		}
	}

	private void RebuildYearChips()
	{
		YearChips.Clear();
		for (int i = _yearPageStart; i < _yearPageStart + 9; i++)
		{
			YearChips.Add(new BoardChip
			{
				Label = i + "年",
				Number = i,
				Start = new DateTime(i, 1, 1),
				End = new DateTime(i, 12, 31)
			});
		}
	}

	private void UpdateBoardChipSelection()
	{
		foreach (BoardChip weekChip in WeekChips)
		{
			weekChip.IsSelected = _boardDate.Date >= weekChip.Start.Date && _boardDate.Date <= weekChip.End.Date;
		}
		foreach (BoardChip monthChip in MonthChips)
		{
			monthChip.IsSelected = _boardDate.Year == monthChip.Start.Year && _boardDate.Month == monthChip.Number;
		}
		foreach (BoardChip yearChip in YearChips)
		{
			yearChip.IsSelected = _boardDate.Year == yearChip.Number;
		}
	}

	private void BuildCalendarDays()
	{
		CalendarDays.Clear();
		DateTime sessionDate = _sessionDate;
		Dictionary<string, DailyRecord> dictionary = _ledger.Records.GroupBy((DailyRecord r) => r.Date, StringComparer.Ordinal).ToDictionary((IGrouping<string, DailyRecord> g) => g.Key, (IGrouping<string, DailyRecord> g) => g.First(), StringComparer.Ordinal);
		DateTime dateTime = new DateTime(_boardDate.Year, _boardDate.Month, 1);
		DateTime dateTime2 = (_calendarExpanded ? dateTime.AddDays(-((int)(dateTime.DayOfWeek + 6) % 7)) : _boardDate.AddDays(-((int)(_boardDate.DayOfWeek + 6) % 7)));
		int num = (_calendarExpanded ? 42 : 7);
		for (int num2 = 0; num2 < num; num2++)
		{
			DateTime date = dateTime2.AddDays(num2);
			string text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			dictionary.TryGetValue(text, out var value);
			bool flag = date.Date > sessionDate;
			string amountText;
			if (Config.IsWorkday(date))
			{
				if (flag)
				{
					amountText = "—";
				}
				else
				{
					amountText = ((value == null || (value.NormalEarned <= 0m && value.WorkedSeconds <= 0.0 && value.OvertimeSeconds <= 0.0)) ? "—" : ("¥" + value.NormalEarned.ToString("F0", CultureInfo.InvariantCulture)));
				}
			}
			else
			{
				amountText = "休息";
			}
			bool flag2 = false;
			bool flag3 = false;
			bool flag4 = false;
			if (!flag && value != null)
			{
				flag2 = value.OvertimeLoss > 0m || value.RoundingLoss > 0m;
				decimal num3 = value.SlackingEarned - value.OvertimeLoss - value.RoundingLoss;
				bool flag5 = value.WorkedSeconds + value.OvertimeSeconds > 0.0 && value.OvertimeLoss <= 0m && value.RoundingLoss <= 0m && value.WorkedSeconds >= Config.ComputedStandardHours * 3600.0 * 0.98;
				flag3 = (num3 > 0m) | flag5;
				flag4 = value.SlackingSeconds > 0.0 || value.IsHoliday;
			}
			if (!flag && value == null)
			{
				flag4 = Config.HolidayCalendar.Holidays.ContainsKey(text) || (Config.TodayOff && Config.TodayOffDate == text);
			}
			int num4 = 2;
			bool orangeDot = flag2 && num4-- > 0;
			bool blueDot = flag3 && num4-- > 0;
			bool grayDot = flag4 && num4 > 0;
			CalendarDays.Add(new BoardCalendarDay
			{
				Date = date,
				DayText = date.Day.ToString(CultureInfo.InvariantCulture),
				IsSelected = (date.Date == _boardDate.Date),
				IsToday = (date.Date == sessionDate),
				IsFuture = flag,
				IsOtherMonth = (_calendarExpanded && date.Month != _boardDate.Month),
				OrangeDot = orangeDot,
				BlueDot = blueDot,
				GrayDot = grayDot,
				AmountText = amountText
			});
		}
	}

	private DailyRecord? GetRecordForDate(DateTime date)
	{
		string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		return _ledger.Records.FirstOrDefault((DailyRecord r) => r.Date == key);
	}

	private void SelectBoardDay(object? parameter)
	{
		DateTime? dateTime;
		if (parameter is DateTime value)
		{
			dateTime = value;
		}
		else
		{
			dateTime = ((!(parameter is BoardCalendarDay boardCalendarDay)) ? ((DateTime?)null) : new DateTime?(boardCalendarDay.Date));
		}
		DateTime? dateTime2 = dateTime;
		if (dateTime2.HasValue)
		{
			_boardDate = dateTime2.Value.Date;
			RefreshBoard();
		}
	}

	private void ToggleCalendar()
	{
		_calendarExpanded = !_calendarExpanded;
		BuildCalendarDays();
		OnPropertyChanged("IsCalendarExpanded");
		OnPropertyChanged("CalendarToggleGlyph");
	}

	private void ShiftWeek(int direction)
	{
		_boardDate = _boardDate.AddDays(7 * direction);
		if (_weekChipsMonth != default(DateTime) && (_boardDate.Year != _weekChipsMonth.Year || _boardDate.Month != _weekChipsMonth.Month))
		{
			_weekChipsMonth = new DateTime(_boardDate.Year, _boardDate.Month, 1);
		}
		RefreshBoard();
	}

	private void ShiftCalendarMonth(int direction)
	{
		_boardDate = new DateTime(_boardDate.Year, _boardDate.Month, 1).AddMonths(direction);
		RefreshBoard();
	}

	private void SelectWeekChip(BoardChip? chip)
	{
		if (chip != null)
		{
			_boardDate = chip.Start.Date;
			RefreshBoard();
		}
	}

	private void SelectMonthChip(BoardChip? chip)
	{
		if (chip != null)
		{
			_boardDate = new DateTime(_boardDate.Year, chip.Number, 1);
			RefreshBoard();
		}
	}

	private void SelectYearChip(BoardChip? chip)
	{
		if (chip != null)
		{
			_boardDate = new DateTime(chip.Number, 1, 1);
			_yearPageStart = chip.Number - 4;
			RefreshBoard();
		}
	}

	private void ShiftYear(int direction)
	{
		if (_viewMode == ViewMode.Monthly)
		{
			_boardDate = new DateTime(_boardDate.Year + direction, _boardDate.Month, 1);
			RefreshBoard();
		}
		else if (_viewMode == ViewMode.Yearly)
		{
			_yearPageStart += direction * 9;
			RebuildYearChips();
			UpdateBoardChipSelection();
		}
	}

	private void ToggleDetail()
	{
		_detailOpen = !_detailOpen;
		if (_detailOpen)
		{
			BuildDetailRecords();
		}
		OnPropertyChanged("IsDetailOpen");
		OnPropertyChanged("DetailPeriodText");
		OnPropertyChanged("HasDetailRecords");
	}

	private void CloseDetail()
	{
		_detailOpen = false;
		OnPropertyChanged("IsDetailOpen");
	}

	private void BuildDetailRecords()
	{
		DetailRecords.Clear();
		(DateTime Start, DateTime End) range = StorageService.GetRange(_viewMode, _boardDate);
		DateTime item = range.Start;
		DateTime item2 = range.End;
		string startText = item.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		string endText = item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		foreach (DailyRecord item3 in _ledger.Records.Where((DailyRecord r) => string.CompareOrdinal(r.Date, startText) >= 0 && string.CompareOrdinal(r.Date, endText) <= 0).OrderBy((DailyRecord r) => r.Date, StringComparer.Ordinal))
		{
			DetailRecords.Add(item3);
		}
		OnPropertyChanged("HasDetailRecords");
		OnPropertyChanged("DetailPeriodText");
	}

	public void SetBoardDate(DateTime date)
	{
		_boardDate = date.Date;
		RefreshBoard();
	}

	public void SetCalendarExpanded(bool expanded)
	{
		if (_calendarExpanded != expanded)
		{
			_calendarExpanded = expanded;
			BuildCalendarDays();
			OnPropertyChanged("IsCalendarExpanded");
			OnPropertyChanged("CalendarToggleGlyph");
		}
	}

	public void ShowDetail()
	{
		if (!_detailOpen)
		{
			_detailOpen = true;
			BuildDetailRecords();
			OnPropertyChanged("IsDetailOpen");
			OnPropertyChanged("DetailPeriodText");
			OnPropertyChanged("HasDetailRecords");
		}
	}

	public void SetAnalysisExpanded(bool expanded)
	{
		if (_analysisExpanded != expanded)
		{
			_analysisExpanded = expanded;
			OnPropertyChanged("IsAnalysisExpanded");
		}
	}

	public void ExportCurrentRangeCsv(string path)
	{
		Persist();
		(DateTime Start, DateTime End) range = StorageService.GetRange(_viewMode, _boardDate);
		DateTime item = range.Start;
		DateTime item2 = range.End;
		string startText = item.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		string endText = item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		List<DailyRecord> records = _ledger.Records.Where((DailyRecord r) => string.CompareOrdinal(r.Date, startText) >= 0 && string.CompareOrdinal(r.Date, endText) <= 0).OrderBy((DailyRecord r) => r.Date, StringComparer.Ordinal).ToList();
		_storage.ExportCsv(path, records);
		SettingsMessage = "✅ 已导出当前周期：" + Path.GetFileName(path);
	}

	private void ToggleAnalysis()
	{
		_analysisExpanded = !_analysisExpanded;
		OnPropertyChanged("IsAnalysisExpanded");
	}

	private void SetLegend(bool show)
	{
		if (Config.ShowCalendarLegend != show)
		{
			Config.ShowCalendarLegend = show;
			_storage.SaveConfig(Config);
			OnPropertyChanged("ShowCalendarLegend");
		}
	}

	private static string ChineseNumber(int number)
	{
		return number switch
		{
			1 => "一", 
			2 => "二", 
			3 => "三", 
			4 => "四", 
			5 => "五", 
			6 => "六", 
			_ => number.ToString(CultureInfo.InvariantCulture), 
		};
	}

	public void RefreshHistory()
	{
		SyncToday();
		History.Clear();
		foreach (DailyRecord item in _ledger.Records.OrderByDescending((DailyRecord r) => r.Date, StringComparer.Ordinal).Take(180))
		{
			History.Add(item);
		}
		OnPropertyChanged("HasHistory");
	}

	private void BuildHolidayDays()
	{
		HolidayDays.Clear();
		DateTime dateTime = new DateTime(_holidayYear, _holidayMonth, 1);
		int num = (int)(dateTime.DayOfWeek + 6) % 7;
		DateTime dateTime2 = dateTime.AddDays(-num);
		DateTime sessionDate = _sessionDate;
		for (int i = 0; i < 42; i++)
		{
			DateTime dateTime3 = dateTime2.AddDays(i);
			if (dateTime3.Month != _holidayMonth)
			{
				HolidayDays.Add(new HolidayCalendarDay
				{
					IsPlaceholder = true,
					Date = dateTime3,
					DayText = string.Empty
				});
				continue;
			}
			string text = dateTime3.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			bool flag = Config.HolidayCalendar.Holidays.TryGetValue(text, out string value);
			bool flag2 = Config.HolidayCalendar.Workdays.Contains(text);
			bool flag3 = Config.HolidayCalendar.RegularRestDays.Contains(text);
			bool flag4 = Config.IsWorkday(dateTime3);
			string text2;
			if (flag)
			{
				text2 = "休";
			}
			else
			{
				text2 = (flag2 ? "班" : string.Empty);
			}
			string tooltip;
			if (flag)
			{
				tooltip = text + " · " + value + "（停计）";
			}
			else if (flag2)
			{
				tooltip = text + " · 调休补班 / 强制上班";
			}
			else if (flag3)
			{
				tooltip = text + " · 常规休息（灰）";
			}
			else
			{
				tooltip = (flag4 ? (text + " · 工作日") : (text + " · 常规休息（灰）"));
			}
			HolidayDays.Add(new HolidayCalendarDay
			{
				Date = dateTime3,
				DayText = dateTime3.Day.ToString(CultureInfo.InvariantCulture),
				IsToday = (dateTime3 == sessionDate),
				BadgeText = text2,
				HasBadge = (text2.Length > 0),
				BadgeBackground = (flag ? BrushRed : (flag2 ? BrushBlue : Brushes.Transparent)),
				BadgeForeground = BrushWhite,
				CellBackground = (flag ? BrushHolidaySoft : (flag2 ? BrushWorkSoft : (flag4 ? BrushWhite : BrushRestSoft))),
				DayForeground = (flag ? BrushRed : (flag2 ? BrushBlue : (flag4 ? BrushInk : BrushGray))),
				Tooltip = tooltip
			});
		}
		OnPropertyChanged("HolidayYearLabel");
		OnPropertyChanged("HolidayMonthLabel");
		OnPropertyChanged("HolidaySummaryText");
		OnPropertyChanged("HolidayQuotaText");
		OnPropertyChanged("ScheduleSummaryText");
	}

	private int SelectedMonthlyRestCount()
	{
		string prefix = $"{_holidayYear:D4}-{_holidayMonth:D2}-";
		return Enumerable.Count(Config.HolidayCalendar.RegularRestDays, (string k) => k.StartsWith(prefix, StringComparison.Ordinal));
	}

	private void ShiftHolidayMonth(int direction)
	{
		DateTime dateTime = new DateTime(_holidayYear, _holidayMonth, 1).AddMonths(direction);
		_holidayYear = dateTime.Year;
		_holidayMonth = dateTime.Month;
		BuildHolidayDays();
	}

	private void ShiftHolidayYear(int direction)
	{
		int num = Math.Clamp(_holidayYear + direction, 2000, 2100);
		if (num != _holidayYear)
		{
			_holidayYear = num;
			BuildHolidayDays();
		}
	}

	private void ToggleHolidayDay(HolidayCalendarDay? day)
	{
		if (day == null || day.IsPlaceholder)
		{
			return;
		}
		string text = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		HolidayConfig holidayCalendar = Config.HolidayCalendar;
		if (holidayCalendar.Holidays.ContainsKey(text))
		{
			holidayCalendar.Holidays.Remove(text);
			holidayCalendar.RegularRestDays.Remove(text);
			holidayCalendar.Workdays.Add(text);
			SetHolidaySyncMessage($"✅ {day.Date:MM-dd} 已设为上班（补班计费）", isError: false);
		}
		else if (holidayCalendar.Workdays.Contains(text))
		{
			holidayCalendar.Workdays.Remove(text);
			SetHolidaySyncMessage($"✅ {day.Date:MM-dd} 已恢复默认排班", isError: false);
		}
		else if (holidayCalendar.RegularRestDays.Contains(text))
		{
			holidayCalendar.RegularRestDays.Remove(text);
			SetHolidaySyncMessage($"✅ {day.Date:MM-dd} 已恢复默认排班", isError: false);
		}
		else if (!Config.IsWorkday(day.Date))
		{
			holidayCalendar.RegularRestDays.Remove(text);
			holidayCalendar.Workdays.Add(text);
			SetHolidaySyncMessage($"✅ {day.Date:MM-dd} 已设为上班（补班计费）", isError: false);
		}
		else
		{
			int monthlyRestQuota = Config.MonthlyRestQuota;
			if (monthlyRestQuota > 0 && SelectedMonthlyRestCount() >= monthlyRestQuota)
			{
				SetHolidaySyncMessage($"⚠\ufe0f 本月月休名额已满（{monthlyRestQuota} 天），请先取消一天再选。", isError: true);
				return;
			}
			holidayCalendar.Workdays.Remove(text);
			holidayCalendar.RegularRestDays.Add(text);
			SetHolidaySyncMessage($"✅ {day.Date:MM-dd} 已设为常规休息（停计）", isError: false);
		}
		_storage.SaveConfig(Config);
		Persist();
		BuildHolidayDays();
		RefreshBoard();
		RaiseAll();
	}

	private async Task SyncHolidaysAsync()
	{
		if (_isHolidaySyncing)
		{
			return;
		}
		_isHolidaySyncing = true;
		OnPropertyChanged("IsHolidaySyncing");
		OnPropertyChanged("HolidaySyncButtonText");
		CommandManager.InvalidateRequerySuggested();
		SetHolidaySyncMessage("⏳ 正在同步法定节假日…", isError: false);
		try
		{
			int syncYear = _holidayYear;
			HolidaySyncResult holidaySyncResult = await _holidayService.SyncYearAsync(syncYear);
			if (!holidaySyncResult.Success)
			{
				SetHolidaySyncMessage("❌ " + (string.IsNullOrWhiteSpace(holidaySyncResult.Message) ? "同步失败，已保留原配置。" : holidaySyncResult.Message), isError: true);
				return;
			}
			string prefix = syncYear.ToString(CultureInfo.InvariantCulture) + "-";
			HolidayConfig holidayCalendar = Config.HolidayCalendar;
			foreach (string item in holidayCalendar.Holidays.Keys.Where((string k) => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
			{
				holidayCalendar.Holidays.Remove(item);
			}
			foreach (string item2 in holidayCalendar.Workdays.Where((string k) => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
			{
				holidayCalendar.Workdays.Remove(item2);
			}
			foreach (KeyValuePair<string, string> holiday in holidaySyncResult.Holidays)
			{
				holidayCalendar.Holidays[holiday.Key] = holiday.Value;
			}
			foreach (string workday in holidaySyncResult.Workdays)
			{
				holidayCalendar.Workdays.Add(workday);
			}
			_storage.SaveConfig(Config);
			Persist();
			BuildHolidayDays();
			RefreshBoard();
			RaiseAll();
			string value = ((holidaySyncResult.StatutoryDays > 0) ? $"（法定 {holidaySyncResult.StatutoryDays} 天）" : string.Empty);
			SetHolidaySyncMessage($"✓ 已载入 {holidaySyncResult.Year} 年放假安排：休 {holidaySyncResult.Holidays.Count} 天 · 班 {holidaySyncResult.Workdays.Count} 天{value}", isError: false);
		}
		catch (Exception ex)
		{
			SetHolidaySyncMessage("❌ 同步失败：" + ex.Message, isError: true);
		}
		finally
		{
			_isHolidaySyncing = false;
			OnPropertyChanged("IsHolidaySyncing");
			OnPropertyChanged("HolidaySyncButtonText");
			CommandManager.InvalidateRequerySuggested();
		}
	}

	private void ResetHolidayYear()
	{
		string prefix = _holidayYear.ToString(CultureInfo.InvariantCulture) + "-";
		HolidayConfig holidayCalendar = Config.HolidayCalendar;
		foreach (string item in holidayCalendar.Holidays.Keys.Where((string k) => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
		{
			holidayCalendar.Holidays.Remove(item);
		}
		foreach (string item2 in holidayCalendar.Workdays.Where((string k) => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
		{
			holidayCalendar.Workdays.Remove(item2);
		}
		foreach (string item3 in holidayCalendar.RegularRestDays.Where((string k) => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
		{
			holidayCalendar.RegularRestDays.Remove(item3);
		}
		_storage.SaveConfig(Config);
		Persist();
		BuildHolidayDays();
		RefreshBoard();
		RaiseAll();
		SetHolidaySyncMessage($"✓ 已清空 {_holidayYear} 年特殊日期（工资与休息制度未变）", isError: false);
	}

	private void ChangeBigSmallWeek()
	{
		if (Config.RestSystem == RestSystem.BigSmallWeek)
		{
			DateTime dateTime = UserConfig.WeekMonday(_sessionDate);
			Func<DateTime, bool?> func = BigSmallWeekPromptRequested;
			bool? flag = ((func == null) ? new bool?(true) : func(dateTime));
			if (flag.HasValue)
			{
				Config.HolidayCalendar.BigSmallWeekAnchorMonday = dateTime;
				Config.HolidayCalendar.BigSmallWeekAnchorIsBig = flag.Value;
				_storage.SaveConfig(Config);
				Persist();
				BuildHolidayDays();
				RefreshBoard();
				RaiseAll();
				SetHolidaySyncMessage("✅ 本周已设为" + (flag.Value ? "大周（周六日休 2 天）" : "小周（仅周日休 1 天）"), isError: false);
			}
		}
	}

	private void SetHolidaySyncMessage(string text, bool isError)
	{
		_holidaySyncMessage = text;
		_holidaySyncBrush = (isError ? BrushRed : BrushGreen);
		OnPropertyChanged("HolidaySyncMessage");
		OnPropertyChanged("HolidaySyncBrush");
		OnPropertyChanged("HasHolidaySyncMessage");
	}

	public void SaveSettings()
	{
		if (Config.SalaryAmount <= 0m)
		{
			SettingsMessage = "❌ 薪资金额必须大于 0。";
			return;
		}
		if (!TryParseTime(Config.WorkStart, out var value) || !TryParseTime(Config.WorkEnd, out var value2))
		{
			SettingsMessage = "❌ 上下班时间格式应为 HH:mm，例如 09:00。";
			return;
		}
		if (value2 <= value)
		{
			SettingsMessage = "❌ 下班时间必须晚于上班时间。";
			return;
		}
		if (!TryParseTime(Config.LunchStart, out var value3) || !TryParseTime(Config.LunchEnd, out var value4) || value4 < value3)
		{
			SettingsMessage = "❌ 午休起止时间有误，且结束时间不能早于开始时间（相等表示没有午休）。";
			return;
		}
		if (Config.WorkDays.Count == 0)
		{
			SettingsMessage = "❌ 至少勾选一个工作日。";
			return;
		}
		if (Config.OvertimeBaseSalary < 0m)
		{
			SettingsMessage = "❌ 加班基准底薪不能为负数。";
			return;
		}
		if (Config.UseCustomOvertimeBase && Config.OvertimeBaseSalary <= 0m)
		{
			SettingsMessage = "❌ 开启自定义加班基数后，加班基准底薪必须大于 0。";
			return;
		}
		if (Config.OvertimeRateNormal <= 0m || Config.OvertimeRateNormalLate <= 0m || Config.OvertimeRateHoliday <= 0m || Config.OvertimeRateHolidayLate <= 0m)
		{
			SettingsMessage = "❌ 加班倍率必须大于 0。";
			return;
		}
		if (Config.NormalOvertimeStageHours <= 0 || Config.HolidayOvertimeStageHours <= 0)
		{
			SettingsMessage = "❌ 加班切换门槛必须大于 0 小时。";
			return;
		}
		Config.WorkStart = $"{value.Hours:00}:{value.Minutes:00}";
		Config.WorkEnd = $"{value2.Hours:00}:{value2.Minutes:00}";
		Config.LunchStart = $"{value3.Hours:00}:{value3.Minutes:00}";
		Config.LunchEnd = $"{value4.Hours:00}:{value4.Minutes:00}";
		Config.StandardHours = Config.ComputedStandardHours;
		if (!_storage.SaveConfig(Config))
		{
			SettingsMessage = "❌ 保存失败：" + _storage.LastError;
			return;
		}
		int num = RecalculateAllOvertimeLosses();
		Persist();
		RefreshBoard();
		_forceRefresh = true;
		OnPropertyChanged("Config");
		SettingsMessage = "✅ 已保存并立即生效（" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "）" + ((num > 0) ? $"；已重算 {num} 条历史加班记录" : string.Empty);
		UpdateSettingsPreview();
		RaiseAll();
		ReportNeeded?.Invoke();
	}

	public void SaveSalaryOnly()
	{
		if (Config.SalaryAmount <= 0m)
		{
			SettingsMessage = "❌ 薪资金额必须大于 0。";
			return;
		}
		if (!_storage.SaveConfig(Config))
		{
			SettingsMessage = "❌ 薪资保存失败：" + _storage.LastError;
			return;
		}
		int num = RecalculateAllOvertimeLosses();
		Persist();
		RefreshBoard();
		UpdateSettingsPreview();
		_forceRefresh = true;
		SettingsMessage = "✅ 薪资已保存并立即生效（" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "）" + ((num > 0) ? $"；已重算 {num} 条加班记录" : string.Empty);
		RaiseAll();
		ReportNeeded?.Invoke();
	}

	private int RecalculateAllOvertimeLosses()
	{
		int changed = 0;
		foreach (DailyRecord record in _ledger.Records)
		{
			if (!DateTime.TryParseExact(record.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
			{
				continue;
			}
			bool holiday = record.HolidayOvertime;
			double seconds = record.OvertimeSeconds;
			if (seconds <= 0.01 && record.ClockOutAt is DateTime clockOut)
			{
				if (record.HolidayOvertime && record.HolidayOvertimeStart is DateTime holidayStart)
				{
					seconds = Math.Max(0.0, (clockOut - holidayStart).TotalSeconds);
				}
				else if (Config.IsWorkday(date))
				{
					DateTime end = At(date, Config.WorkEnd);
					if (clockOut > end)
					{
						seconds = (clockOut - end).TotalSeconds;
					}
				}
			}
			EnsureEmergencySessions(record);
			double emergencySeconds = record.EmergencyCallSessions.Sum((EmergencyCallSession s) => Math.Max(0.0, s.Seconds));
			if (seconds < emergencySeconds)
			{
				seconds = emergencySeconds;
			}
			if (seconds <= 0.01)
			{
				continue;
			}
			if (record.OvertimeSeconds != seconds)
			{
				record.OvertimeSeconds = seconds;
			}
			double regularSeconds = Math.Max(0.0, seconds - emergencySeconds);
			decimal emergencyLoss = EmergencyLossFromSessions(record, date);
			decimal newLoss = OvertimeLossFor(regularSeconds, holiday) + emergencyLoss;
			bool isChanged = record.OvertimeLoss != newLoss || record.EmergencyCallLoss != emergencyLoss || record.EmergencyCallSeconds != emergencySeconds;
			record.OvertimeLoss = newLoss;
			record.EmergencyCallLoss = emergencyLoss;
			record.EmergencyCallSeconds = emergencySeconds;
			record.EmergencyCallEarned = record.EmergencyCallSessions.Sum((EmergencyCallSession s) => s.Earned);
			record.EmergencyCallCount = record.EmergencyCallSessions.Count;
			if (isChanged)
			{
				changed++;
			}
		}
		return changed;
	}

	public void RefreshDerivedSettings()
	{
		UpdateSettingsPreview();
	}

	private void UpdateSettingsPreview()
	{
		OnPropertyChanged("SettingsPreviewText");
		OnPropertyChanged("StandardHoursDisplay");
		OnPropertyChanged("DailyWageText");
		OnPropertyChanged("HourlyWageText");
		OnPropertyChanged("MinuteWageText");
		OnPropertyChanged("SecondWageText");
		OnPropertyChanged("OvertimeBaseHourlyDisplay");
	}

	private bool HasWorkDay(DayOfWeek day)
	{
		return Config.WorkDays.Contains(day);
	}

	private void SetWorkDay(DayOfWeek day, bool value)
	{
		bool flag = Config.WorkDays.Contains(day);
		if (value && !flag)
		{
			Config.WorkDays.Add(day);
		}
		if (!value & flag)
		{
			Config.WorkDays.Remove(day);
		}
		Config.WorkDays = (from d in Config.WorkDays.Distinct()
			orderby (int)d
			select d).ToList();
		OnPropertyChanged("WorkMon");
		OnPropertyChanged("WorkTue");
		OnPropertyChanged("WorkWed");
		OnPropertyChanged("WorkThu");
		OnPropertyChanged("WorkFri");
		OnPropertyChanged("WorkSat");
		OnPropertyChanged("WorkSun");
	}

	private static List<string> ParseDateList(string? text)
	{
		List<string> list = new List<string>();
		if (string.IsNullOrWhiteSpace(text))
		{
			return list;
		}
		string[] array = text.Split(new char[8] { '\r', '\n', ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].Trim();
			if (text2.Length != 0 && DateTime.TryParseExact(text2, new string[4] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
			{
				list.Add(result.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
			}
		}
		return list.Distinct().OrderBy((string x) => x, StringComparer.Ordinal).ToList();
	}

	public bool IsWorkday(DateTime date)
	{
		return Config.IsWorkday(date);
	}

	public DateTime BusinessDate(DateTime now)
	{
		DateTime dateTime = At(now.Date, Config.WorkStart).AddHours(-1.0);
		if (!(now < dateTime))
		{
			return now.Date;
		}
		return now.Date.AddDays(-1.0);
	}

	private DateTime AtBusiness(DateTime now, string hhmm)
	{
		return At(BusinessDate(now), hhmm);
	}

	private static DateTime At(DateTime day, string hhmm)
	{
		TimeSpan timeSpan = (TryParseTime(hhmm, out var value) ? value : TimeSpan.Zero);
		return day.Date + timeSpan;
	}

	private static bool TryParseTime(string? text, out TimeSpan value)
	{
		if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value) && value >= TimeSpan.Zero)
		{
			return value < TimeSpan.FromDays(1.0);
		}
		return false;
	}

	private static string Money(decimal value, int digits)
	{
		return value.ToString("F" + digits, CultureInfo.InvariantCulture);
	}

	private static string Duration(double seconds)
	{
		TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
		if (timeSpan.TotalHours >= 1.0)
		{
			return $"{(int)timeSpan.TotalHours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
		}
		return $"{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
	}

	private static string Clock(double seconds)
	{
		TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
		return $"{(int)timeSpan.TotalMinutes:00}:{timeSpan.Seconds:00}";
	}

	private static string ConvertToGoods(decimal amount)
	{
		if (amount <= 0m)
		{
			return "还没薅到，快去摸一会儿";
		}
		decimal value = amount / 15m;
		decimal value2 = amount / 18m;
		return $"≈ {value:F1} 杯奶茶 · {value2:F1} 顿猪脚饭";
	}

	private static SlackingType ParseSlackType(object? parameter)
	{
		if (!(parameter is string value) || !Enum.TryParse<SlackingType>(value, ignoreCase: true, out var result))
		{
			return SlackingType.Phone;
		}
		return result;
	}

	private static int ParseInt(object? parameter)
	{
		if (!(parameter is string s) || !int.TryParse(s, out var result))
		{
			return 0;
		}
		return result;
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
	}

	private void RaiseAll()
	{
		string[] livePropertyNames = LivePropertyNames;
		foreach (string name in livePropertyNames)
		{
			OnPropertyChanged(name);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_timer.Stop();
			_timer.Tick -= OnTick;
		}
	}
}
