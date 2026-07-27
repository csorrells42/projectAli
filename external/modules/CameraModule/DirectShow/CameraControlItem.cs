using System;

namespace AvatarBuilder.Modules.Webcam.DirectShow;

public sealed class CameraControlItem
{
	public CameraControlKind Kind { get; }

	public int PropertyId { get; }

	public string Name { get; }

	public int Minimum { get; }

	public int Maximum { get; }

	public int Step { get; }

	public int DefaultValue { get; }

	public int Value { get; set; }

	public bool IsAuto { get; set; }

	public bool SupportsAuto { get; }

	public CameraControlItem(CameraControlKind kind, int propertyId, string name, int minimum, int maximum, int step, int defaultValue, int value, bool isAuto, bool supportsAuto)
	{
		Kind = kind;
		PropertyId = propertyId;
		Name = name;
		Minimum = minimum;
		Maximum = maximum;
		Step = Math.Max(1, step);
		DefaultValue = defaultValue;
		Value = value;
		IsAuto = isAuto;
		SupportsAuto = supportsAuto;
	}
}
