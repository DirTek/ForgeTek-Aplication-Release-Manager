using System.Windows;
using System.Windows.Controls;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class NoChangesDialog : UserControl
{
    public NoChangesDialog()
    {
        InitializeComponent();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        win?.Close();
    }
}
