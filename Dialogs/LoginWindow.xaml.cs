using System.Windows;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Dialogs;

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
            ErrorText.Text = "Incorrect username or password.";
            ErrorText.Visibility = Visibility.Visible;
            PasswordBox.Clear();
            PasswordBox.Focus();
            return;
        }
        AuthenticatedUser = user;
        DialogResult = true;
    }
}
