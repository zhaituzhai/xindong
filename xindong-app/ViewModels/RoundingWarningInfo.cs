namespace XinDong.ViewModels;

public sealed class RoundingWarningInfo
{
	public double RemainingSeconds { get; }

	public decimal ExtraAmount { get; }

	public string Title { get; }

	public string Message { get; }

	public RoundingWarningInfo(double remainingSeconds, decimal extraAmount, string title, string message)
	{
		RemainingSeconds = remainingSeconds;
		ExtraAmount = extraAmount;
		Title = title;
		Message = message;
	}
}
