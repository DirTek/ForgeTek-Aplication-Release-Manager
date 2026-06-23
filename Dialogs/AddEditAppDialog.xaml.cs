using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class AddEditAppDialog : Window
{
    public string AppName         => NameBox.Text.Trim();
    public string AppPath         => PathBox.Text.Trim();
    public string SolutionPath    => SlnBox.Text.Trim();
    public string InitialVersion  => VersionBox.Text.Trim();
    public string AccentColor     { get; private set; } = "#0A84FF";
    public bool   IsNewApp        { get; }

    // Localized string lookup for code-behind (this dialog is created with 'new', not via DI).
    // Reads from the merged Strings/<culture>.xaml; returns the key if missing (never crashes).
    private string L(string key) => TryFindResource(key) as string ?? key;
    private string L(string key, params object[] args) => string.Format(L(key), args);

    public AddEditAppDialog(string name = "", string path = "", string accentColor = "#0A84FF",
        string solutionPath = "")
    {
        InitializeComponent();
        IsNewApp = string.IsNullOrEmpty(name);

        AccentColor = accentColor;
        ColorPicker.SelectedColor = ParseHex(accentColor);

        NameBox.Text = name;
        PathBox.Text = path;
        SlnBox.Text = solutionPath;

        if (!IsNewApp)
        {
            ExeLabel.Visibility   = Visibility.Collapsed;
            ExeBox.Visibility     = Visibility.Collapsed;
            VersionLabel.Visibility = Visibility.Collapsed;
            VersionBox.Visibility   = Visibility.Collapsed;
            SaveBtn.Content         = L("Str.AddApp.SaveButton");
            Title                   = L("Str.AddApp.EditTitle");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L("Str.AddApp.SelectFolder") };
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            _ = ScanForExesAsync(dlg.FolderName);
        }
    }

    private void BrowseSln_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L("Str.AddApp.SelectSolution"),
            Filter = "Solution / project (*.sln;*.csproj)|*.sln;*.csproj|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(SlnBox.Text))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(SlnBox.Text); } catch { }
        }
        else if (!string.IsNullOrWhiteSpace(PathBox.Text))
        {
            dlg.InitialDirectory = PathBox.Text;
        }
        if (dlg.ShowDialog(this) == true)
            SlnBox.Text = dlg.FileName;
    }

    private async Task ScanForExesAsync(string folder)
    {
        if (!IsNewApp) return;

        ScanProgress.Visibility = Visibility.Visible;
        ExeBox.IsEnabled        = false;
        ExeBox.ItemsSource      = null;

        List<string> exes;
        try
        {
            exes = await Task.Run(() =>
                Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
                         .Select(f => Path.GetRelativePath(folder, f))
                         .OrderBy(f => f)
                         .ToList());
        }
        catch (Exception ex)
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanErrorText.Text = L("Str.AddApp.ScanFailed", ex.Message);
            ScanErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (exes.Count == 0)
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ExeBox.IsEnabled        = false;
            return;
        }

        ExeBox.ItemsSource = exes;

        if (exes.Count == 1)
            ExeBox.SelectedIndex = 0;

        ScanProgress.Visibility = Visibility.Collapsed;
        ExeBox.IsEnabled        = true;
    }

    private void ExeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ExeBox.SelectedItem is not string relativePath) return;

        var fullPath = Path.Combine(PathBox.Text.Trim(), relativePath);
        try
        {
            var version = FileVersionInfo.GetVersionInfo(fullPath).FileVersion;
            if (!string.IsNullOrWhiteSpace(version))
                VersionBox.Text = version;
        }
        catch
        {
            // ignore — keep existing version text
        }
    }

    private void ColorPicker_ColorChanged(object sender, EventArgs e)
    {
        var c = ColorPicker.SelectedColor;
        AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var toggle = FindVisualChild<ToggleButton>(ColorPicker);
        if (toggle != null)
            toggle.IsChecked = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length >= 6)
        {
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return Color.FromRgb(r, g, b);
        }
        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppName))        { ShowError(L("Str.AddApp.ErrNoName")); return; }
        if (string.IsNullOrWhiteSpace(AppPath))         { ShowError(L("Str.AddApp.ErrNoFolder")); return; }
        if (IsNewApp && string.IsNullOrWhiteSpace(InitialVersion))
                                                        { ShowError(L("Str.AddApp.ErrNoVersion")); return; }
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text       = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
