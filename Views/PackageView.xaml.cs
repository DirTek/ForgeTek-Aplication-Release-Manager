using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Views;

public partial class PackageView : UserControl
{
    public PackageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PackageViewModel old)
            old.Log.CollectionChanged -= OnLogChanged;
        if (e.NewValue is PackageViewModel vm)
        {
            vm.Log.CollectionChanged += OnLogChanged;
            PfxPasswordBox.Password = vm.PfxPassword;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogScrollViewer.ScrollToBottom();
    }

    private void PfxPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is PackageViewModel vm)
            vm.PfxPassword = ((PasswordBox)sender).Password;
    }
}
