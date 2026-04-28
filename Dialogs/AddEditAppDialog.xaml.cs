using System.Windows;
using Microsoft.Win32;

namespace ForgeTekUpdatePackager.Dialogs;

public partial class AddEditAppDialog : Window
{
    public string AppName         => NameBox.Text.Trim();
    public string AppPath         => PathBox.Text.Trim();
    public string InitialVersion  => VersionBox.Text.Trim();
    public bool   IsNewApp        { get; }

    public AddEditAppDialog(string name = "", string path = "")
    {
        InitializeComponent();
        IsNewApp = string.IsNullOrEmpty(name);

        NameBox.Text = name;
        PathBox.Text = path;

        if (!IsNewApp)
        {
            VersionLabel.Visibility = Visibility.Collapsed;
            VersionBox.Visibility   = Visibility.Collapsed;
            SaveBtn.Content         = "Save Changes";
            Title                   = "Edit Application";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Application Folder" };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppName))
        {
            MessageBox.Show("Please enter an application name.", "Missing Field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(AppPath))
        {
            MessageBox.Show("Please select a folder.", "Missing Field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsNewApp && string.IsNullOrWhiteSpace(InitialVersion))
        {
            MessageBox.Show("Please enter an initial version number.", "Missing Field",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
