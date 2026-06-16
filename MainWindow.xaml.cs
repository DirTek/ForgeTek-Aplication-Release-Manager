using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private void Setups_Click(object sender, RoutedEventArgs e) => _vm.ToggleSetups();

    // Click an already-selected app to deselect it (returns to the Welcome screen).
    private void AppListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement((ListBox)sender, (DependencyObject)e.OriginalSource)
                is ListBoxItem { IsSelected: true })
        {
            _vm.DeselectApp();
            e.Handled = true;
        }
    }

    // Esc deselects the current app.
    private void AppListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.SelectedApp is not null)
        {
            _vm.DeselectApp();
            e.Handled = true;
        }
    }
}
