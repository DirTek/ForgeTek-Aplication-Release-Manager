using System.Windows;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class ResumePackagingDialog : Window
{
    public enum ResumeChoice { StartOver, Continue }

    public ResumeChoice Choice { get; private set; }

    public ResumePackagingDialog(string versionNumber, string stepLabel)
    {
        InitializeComponent();
        MainText.Text = $"v{versionNumber} was processed up to Step {stepLabel}.";
        SubText.Text = "Do you want to start over from the beginning, or continue from where you left off?";
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
