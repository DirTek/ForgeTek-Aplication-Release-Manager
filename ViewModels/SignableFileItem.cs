using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class SignableFileItem : ObservableObject
{
    public string RelativePath { get; }
    public string FullPath { get; }
    public string BadgeText { get; }
    [ObservableProperty] private bool _isSelected = true;
    public SignableFileItem(SignableFile file)
    {
        RelativePath = file.RelativePath;
        FullPath     = file.FullPath;
        BadgeText    = file.Change switch
        {
            SignableFileChange.Added    => "+",
            SignableFileChange.Modified => "~",
            _                          => string.Empty,
        };
    }
}
