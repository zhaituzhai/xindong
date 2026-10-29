namespace XinDong.ViewModels;

public sealed class OptionItem
{
	public string Name { get; }

	public object Value { get; }

	public OptionItem(string name, object value)
	{
		Name = name;
		Value = value;
	}

	public override string ToString()
	{
		return Name;
	}
}
