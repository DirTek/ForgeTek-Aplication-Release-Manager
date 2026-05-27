using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public enum AddEditAppResult
{
    Cancelled,
    Saved
}

public record AddEditAppData(string Name, string Path, string InitialVersion, string AccentColor = "#0A84FF");

public record ResumePackagingData(bool StartOver);

public interface IDialogService
{
    void Alert(string title, string message);
    bool Confirm(string title, string message, string confirmLabel);
    string? OpenFile(string title, string filter, bool checkFileExists = true);
    string? SaveFile(string title, string filter, string defaultName, string? initialDir = null);
    string? OpenFolder(string title);
    AddEditAppData? ShowAddEditApp(string? existingName = null, string? existingPath = null, string? existingColor = null);
    ResumePackagingData? ShowResumePackagingDialog(string version, string step);
    void ShowBackupDialog(IBackupService backup, string rootFolder, string settingsFilePath);
    void ShowChangelogWindow(string version, string content);
}
