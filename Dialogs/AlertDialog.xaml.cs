using System.Windows;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class AlertDialog : Window
{
    public AlertDialog(string title, string message)
    {
        InitializeComponent();
        Title            = title;
        TitleText.Text   = title;
        MessageText.Text = message;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
