using System.Windows;
using ForgeTekApplicationReleaseManager.ViewModels;

namespace ForgeTekApplicationReleaseManager.Views;

public partial class ChangelogView : Window
{
    public ChangelogView(ChangelogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Owner = Application.Current.MainWindow;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
