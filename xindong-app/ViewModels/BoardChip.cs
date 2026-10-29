using System;
using System.ComponentModel;

namespace XinDong.ViewModels;

public sealed class BoardChip : INotifyPropertyChanged
{
	private bool _isSelected;

	public string Label { get; init; } = string.Empty;

	public int Number { get; init; }

	public DateTime Start { get; init; }

	public DateTime End { get; init; }

	public bool IsSelected
	{
		get
		{
			return _isSelected;
		}
		set
		{
			if (_isSelected != value)
			{
				_isSelected = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsSelected"));
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}
