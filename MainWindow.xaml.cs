using System.Windows;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e) => _vm.AddApp();
    private void Options_Click(object sender, RoutedEventArgs e) => _vm.NavigateToOptions();
    private void Setups_Click(object sender, RoutedEventArgs e) => _vm.NavigateToSetups();
}
