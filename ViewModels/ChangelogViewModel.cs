using CommunityToolkit.Mvvm.ComponentModel;

namespace ForgeTekUpdatePackager.ViewModels;

public partial class ChangelogViewModel : ObservableObject
{
    public string VersionNumber { get; }
    public string Content { get; }

    public ChangelogViewModel(string versionNumber, string content)
    {
        VersionNumber = versionNumber;
        Content = content;
    }
}
