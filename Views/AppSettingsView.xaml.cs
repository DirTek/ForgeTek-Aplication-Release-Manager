using System.Windows;
using System.Windows.Controls;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Views;

public partial class AppSettingsView : UserControl
{
    public AppSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is AppSettingsViewModel vm)
        {
            CertPasswordBox.Password = vm.DefaultCertPassword;
            FtpPasswordBox.Password  = vm.FtpPassword;
        }
    }

    private void CertPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
            vm.DefaultCertPassword = CertPasswordBox.Password;
    }

    private void FtpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
            vm.FtpPassword = FtpPasswordBox.Password;
    }
}
