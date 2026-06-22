using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public enum AddEditAppResult
{
    Cancelled,
    Saved
}

public record AddEditAppData(string Name, string Path, string InitialVersion,
    string AccentColor = "#0A84FF", string SolutionPath = "");

public record ResumePackagingData(bool StartOver);

public interface IDialogService
{
    void Alert(string title, string message);
    bool Confirm(string title, string message, string confirmLabel);
    /// <summary>Prompts for a password. Returns the entered value, or null if cancelled.</summary>
    string? PromptPassword(string title, string message);
    string? OpenFile(string title, string filter, bool checkFileExists = true);
    string? SaveFile(string title, string filter, string defaultName, string? initialDir = null);
    string? OpenFolder(string title);
    AddEditAppData? ShowAddEditApp(string? existingName = null, string? existingPath = null,
        string? existingColor = null, string? existingSolutionPath = null);
    ResumePackagingData? ShowResumePackagingDialog(string version, string step);
    void ShowBackupDialog(IBackupService backup, string rootFolder, string settingsFilePath);
    void ShowChangelogWindow(string version, string content);
}
