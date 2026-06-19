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
            GitHubTokenBox.Password  = vm.GitHubToken;
            SftpPasswordBox.Password = vm.SftpPassword;
            S3SecretKeyBox.Password  = vm.S3SecretKey;
        }
    }

    private void SftpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
            vm.SftpPassword = SftpPasswordBox.Password;
    }

    private void S3SecretKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
            vm.S3SecretKey = S3SecretKeyBox.Password;
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

    private void GitHubTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
            vm.GitHubToken = GitHubTokenBox.Password;
    }
}
