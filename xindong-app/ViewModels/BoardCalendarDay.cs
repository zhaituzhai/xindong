using System;

namespace XinDong.ViewModels;

public sealed class BoardCalendarDay
{
	public DateTime Date { get; init; }

	public string DayText { get; init; } = string.Empty;

	public bool IsSelected { get; init; }

	public bool IsToday { get; init; }

	public bool IsFuture { get; init; }

	public bool IsOtherMonth { get; init; }

	public bool BlueDot { get; init; }

	public bool OrangeDot { get; init; }

	public bool GrayDot { get; init; }

	public string AmountText { get; init; } = "—";
}
