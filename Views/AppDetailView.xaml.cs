using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Views;

public partial class AppDetailView : UserControl
{
    public AppDetailView() => InitializeComponent();

    // Close the "More ▾" popup after an item is chosen (its command still runs).
    private void MoreItem_Click(object sender, RoutedEventArgs e) => MoreBtn.IsChecked = false;

    // Click an already-selected version row to deselect it.
    private void VersionsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // Don't interfere with buttons inside a row (e.g. the "View" changelog button).
        for (var n = source; n is not null && n is not DataGridRow; n = VisualTreeHelper.GetParent(n))
            if (n is ButtonBase) return;

        if (ItemsControl.ContainerFromElement((DataGrid)sender, source) is DataGridRow { IsSelected: true }
            && DataContext is AppDetailViewModel vm)
        {
            vm.SelectedVersion = null;
            e.Handled = true;
        }
    }

    // Esc clears the selected version.
    private void VersionsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is AppDetailViewModel { SelectedVersion: not null } vm)
        {
            vm.SelectedVersion = null;
            e.Handled = true;
        }
    }
}
