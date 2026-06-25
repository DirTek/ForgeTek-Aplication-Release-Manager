using System.Windows;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services;
using static ForgeTekApplicationReleaseManager.Services.LocalizationService;

namespace ForgeTekApplicationReleaseManager.Dialogs;

public partial class LoginWindow : Window
{
    private readonly IUserService _users;

    public AppUser? AuthenticatedUser { get; private set; }

    public LoginWindow(IUserService users)
    {
        _users = users;
        InitializeComponent();
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var user = _users.Authenticate(UsernameBox.Text.Trim(), PasswordBox.Password);
        if (user is null)
        {
            ErrorText.Text = S("Str.LoginCB.IncorrectCreds");
            ErrorText.Visibility = Visibility.Visible;
            PasswordBox.Clear();
            PasswordBox.Focus();
            return;
        }
        AuthenticatedUser = user;
        DialogResult = true;
    }
}
