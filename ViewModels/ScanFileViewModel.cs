using CommunityToolkit.Mvvm.ComponentModel;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.ViewModels;

public partial class ScanFileViewModel(FileRecord record) : ObservableObject
{
    public FileRecord Record { get; } = record;
    public string Path => Record.Path;
    public string DateModified => Record.DateModified.ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty] private bool _isDebug;
}
