using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppUpdater;

public partial class MainWindow : Window, IUpdaterUi
{
    private UpdaterConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _config = UpdaterConfig.Load(AppContext.BaseDirectory);

        Title = _config.WindowTitle;
        TitleText.Text = _config.WindowTitle;
        ApplyAccent(_config.AccentColor);
        TryShowAppIcon();

        UpdateResult result;
        try
        {
            var core = new UpdaterCore(_config, this);
            result = await Task.Run(core.RunAsync);
        }
        catch (Exception ex)
        {
            result = new UpdateResult(false, null, ex.Message);
        }

        if (result.Success && !string.IsNullOrWhiteSpace(result.AppExePath))
        {
            Status("Update complete — restarting…");
            Log("Done. Restarting the application.");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.AppExePath,
                    WorkingDirectory = Path.GetDirectoryName(result.AppExePath),
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Log($"Could not relaunch automatically: {ex.Message}");
            }
            // Brief pause so the user sees the success state, then close.
            await Task.Delay(1200);
            Close();
            return;
        }

        // Failure: surface the error and let the user close.
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        SubtitleText.Text = "Update failed";
        Status(result.Error ?? "The update could not be applied.");
        Log($"ERROR: {result.Error}");
        CloseBtn.Visibility = Visibility.Visible;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ── IUpdaterUi (marshal to the UI thread) ──────────────────────────────────

    public void Status(string text) =>
        Dispatcher.Invoke(() => StatusText.Text = text);

    public void Log(string line) =>
        Dispatcher.Invoke(() =>
        {
            LogText.Text = string.IsNullOrEmpty(LogText.Text) ? line : LogText.Text + "\n" + line;
            LogScroller.ScrollToEnd();
        });

    public void Progress(double fraction) =>
        Dispatcher.Invoke(() =>
        {
            if (fraction < 0)
            {
                Bar.IsIndeterminate = true;
            }
            else
            {
                Bar.IsIndeterminate = false;
                Bar.Value = Math.Clamp(fraction, 0, 1);
            }
        });

    // ── Branding ───────────────────────────────────────────────────────────────

    private void ApplyAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            System.Windows.Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
        }
        catch { /* keep default */ }
    }

    // Extracts the main app's icon (the updater sits next to it) for the header + titlebar.
    private void TryShowAppIcon()
    {
        try
        {
            var appExe = Path.Combine(AppContext.BaseDirectory, _config.AppExe);
            if (string.IsNullOrWhiteSpace(_config.AppExe) || !File.Exists(appExe))
                return;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(appExe);
            if (icon is null) return;

            var src = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            AppIconImage.Source = src;
            AppIconImage.Visibility = Visibility.Visible;
            Icon = src;
        }
        catch { /* branding is best-effort */ }
    }
}
