using System;
using System.Windows.Media;

namespace XinDong.ViewModels;

public sealed class HolidayCalendarDay
{
	public DateTime Date { get; init; }

	public string DayText { get; init; } = string.Empty;

	public bool IsPlaceholder { get; init; }

	public bool IsToday { get; init; }

	public bool HasBadge { get; init; }

	public string BadgeText { get; init; } = string.Empty;

	public Brush BadgeBackground { get; init; } = Brushes.Transparent;

	public Brush BadgeForeground { get; init; } = Brushes.White;

	public Brush CellBackground { get; init; } = Brushes.White;

	public Brush DayForeground { get; init; } = Brushes.Black;

	public string Tooltip { get; init; } = string.Empty;
}
