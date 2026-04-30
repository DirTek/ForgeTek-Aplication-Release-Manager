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
}
