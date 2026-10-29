using System.ComponentModel;
using System.Windows.Media;

namespace XinDong.ViewModels;

public sealed class BoardMetric : INotifyPropertyChanged
{
	private string _valueText = "0";

	private Brush _valueBrush = Brushes.Black;

	private Brush _labelBrush = Brushes.Gray;

	public string Name { get; }

	public string ValueText
	{
		get
		{
			return _valueText;
		}
		set
		{
			if (!(_valueText == value))
			{
				_valueText = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ValueText"));
			}
		}
	}

	public Brush ValueBrush
	{
		get
		{
			return _valueBrush;
		}
		set
		{
			if (_valueBrush != value)
			{
				_valueBrush = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ValueBrush"));
			}
		}
	}

	public Brush LabelBrush
	{
		get
		{
			return _labelBrush;
		}
		set
		{
			if (_labelBrush != value)
			{
				_labelBrush = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("LabelBrush"));
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public BoardMetric(string name)
	{
		Name = name;
	}
}
