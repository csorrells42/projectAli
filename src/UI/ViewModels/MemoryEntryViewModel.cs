using Ali.Modules.Memory;

namespace Ali.UI.ViewModels;

public sealed class MemoryEntryViewModel(MemoryEntry memory) : ObservableObject
{
    public MemoryEntry Memory { get; } = memory;

    public string Id => Memory.MemoryId;

    public string Text => Memory.Text;

    public string Category => Memory.Category;

    public string SourceText => Memory.Source.ToString();

    public string SensitivityText => Memory.Sensitivity.ToString();

    public string UpdatedAtText => Memory.UpdatedAt.ToLocalTime().ToString("g");
}

