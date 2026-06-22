using System.Windows;
using Microsoft.Win32;
using ForgeTekApplicationReleaseManager.Dialogs;
using ForgeTekApplicationReleaseManager.ViewModels;
using ForgeTekApplicationReleaseManager.Views;

namespace ForgeTekApplicationReleaseManager.Services;

public class DialogService : IDialogService
{
    private static Window Owner => Application.Current.MainWindow;

    public void Alert(string title, string message)
        => new AlertDialog(title, message) { Owner = Owner }.ShowDialog();

    public bool Confirm(string title, string message, string confirmLabel)
        => new ConfirmDialog(title, message, confirmLabel) { Owner = Owner }.ShowDialog() == true;

    public string? PromptPassword(string title, string message)
    {
        var dlg = new PasswordPromptDialog(title, message) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.EnteredPassword : null;
    }

    public string? OpenFile(string title, string filter, bool checkFileExists = true)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = checkFileExists,
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultName, string? initialDir = null)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultName,
            InitialDirectory = initialDir ?? string.Empty,
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? OpenFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public AddEditAppData? ShowAddEditApp(string? existingName = null, string? existingPath = null,
        string? existingColor = null, string? existingSolutionPath = null)
    {
        var dlg = existingName is not null
            ? new AddEditAppDialog(existingName, existingPath ?? "", existingColor ?? "#0A84FF", existingSolutionPath ?? "")
            : new AddEditAppDialog();

        dlg.Owner = Owner;
        if (dlg.ShowDialog() != true) return null;

        return new AddEditAppData(dlg.AppName, dlg.AppPath, dlg.InitialVersion, dlg.AccentColor, dlg.SolutionPath);
    }

    public ResumePackagingData? ShowResumePackagingDialog(string version, string step)
    {
        var dlg = new ResumePackagingDialog(version, step) { Owner = Owner };
        if (dlg.ShowDialog() != true) return null;
        return new ResumePackagingData(dlg.Choice == ResumePackagingDialog.ResumeChoice.StartOver);
    }

    public void ShowBackupDialog(IBackupService backup, string rootFolder, string settingsFilePath)
    {
        new BackupDialog(backup, rootFolder, settingsFilePath) { Owner = Owner }.ShowDialog();
    }

    public void ShowChangelogWindow(string version, string content)
    {
        new ChangelogView(new ChangelogViewModel(version, content)).ShowDialog();
    }
}
