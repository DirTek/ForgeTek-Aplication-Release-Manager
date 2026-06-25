using System.Windows;
using static ForgeTekApplicationReleaseManager.Services.LocalizationService;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class ResumePackagingDialog : Window
{
    public enum ResumeChoice { StartOver, Continue }

    public ResumeChoice Choice { get; private set; }

    public ResumePackagingDialog(string versionNumber, string stepLabel)
    {
        InitializeComponent();
        MainText.Text = S("Str.ResumeCB.MainFmt", versionNumber, stepLabel);
        SubText.Text = S("Str.ResumeCB.Sub");
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        Choice = ResumeChoice.StartOver;
        DialogResult = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Choice = ResumeChoice.Continue;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DialogResult == null)
            DialogResult = false;
    }
}
