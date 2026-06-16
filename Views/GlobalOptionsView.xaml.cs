using System.Windows;
using System.Windows.Controls;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Views;

public partial class GlobalOptionsView : UserControl
{
    public GlobalOptionsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is GlobalOptionsViewModel vm)
        {
            CertPasswordBox.Password    = vm.GlobalCertPassword;
            GeneratePasswordBox.Password = string.Empty;
        }
    }

    private void CertPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is GlobalOptionsViewModel vm)
            vm.GlobalCertPassword = ((PasswordBox)sender).Password;
    }

    private void GeneratePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is GlobalOptionsViewModel vm)
        {
            vm.GeneratePassword = ((PasswordBox)sender).Password;
            vm.GenerateCertCommand.NotifyCanExecuteChanged();
        }
    }

    private void NewUserPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is GlobalOptionsViewModel vm)
            vm.NewUserPassword = ((PasswordBox)sender).Password;
    }

    private void ResetPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is GlobalOptionsViewModel vm)
            vm.ResetPasswordValue = ((PasswordBox)sender).Password;
    }

    private void GitHubPatBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is GlobalOptionsViewModel vm)
            vm.GitHubPatValue = ((PasswordBox)sender).Password;
    }
}
