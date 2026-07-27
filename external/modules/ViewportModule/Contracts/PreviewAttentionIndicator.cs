namespace AvatarBuilder.Modules.Viewports.Contracts;

public readonly record struct PreviewAttentionIndicator(
	bool IsAttentive,
	string Label = "ATTENTION");
