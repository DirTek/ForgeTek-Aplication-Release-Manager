using System.Diagnostics;
using System.Windows;
using ForgeTekApplicationReleaseManager.Services;
using static ForgeTekApplicationReleaseManager.Services.LocalizationService;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class GitHubConnectWindow : Window
{
    private readonly IGitHubAuthService _auth;
    private readonly string _clientId;
    private readonly CancellationTokenSource _cts = new();
    private DeviceCodeInfo? _info;

    public string? Token { get; private set; }
    public string? Login { get; private set; }

    public GitHubConnectWindow(IGitHubAuthService auth, string clientId)
    {
        _auth = auth;
        _clientId = clientId;
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task StartAsync()
    {
        try
        {
            _info = await _auth.RequestDeviceCodeAsync(_clientId, "repo", _cts.Token);
            CodeText.Text = _info.UserCode;
            OpenButton.IsEnabled = true;
            StatusText.Text = S("Str.GhConnectCB.WaitingAuth");

            // Open the browser + copy the code automatically to get the user moving.
            OpenVerification();

            Token = await _auth.PollForTokenAsync(_clientId, _info.DeviceCode, _info.Interval, _cts.Token);
            Login = await _auth.GetLoginAsync(Token, _cts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) { /* cancelled / closed */ }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenVerification();

    private void OpenVerification()
    {
        if (_info is null) return;
        try
        {
            Clipboard.SetText(_info.UserCode);
            Process.Start(new ProcessStartInfo(_info.VerificationUri) { UseShellExecute = true });
        }
        catch { /* clipboard/browser best-effort */ }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }

    private void ShowError(string message)
    {
        Spinner.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
