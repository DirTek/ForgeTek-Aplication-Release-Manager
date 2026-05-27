using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class PackageTypeOption : ObservableObject
{
    public PackageType Value { get; }
    public string Label { get; }
    public string Description { get; }
    [ObservableProperty] private bool _isSelected;
    public PackageTypeOption(PackageType value, string label, string description, bool selected = false)
    {
        Value = value; Label = label; Description = description; _isSelected = selected;
    }
}
