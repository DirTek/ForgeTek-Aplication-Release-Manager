namespace ForgeTekUpdatePackager.Models;

/// <summary>A local application user. The password is stored only as a PBKDF2 hash + salt
/// (never plaintext, never reversible).</summary>
public class AppUser
{
    public string Username { get; set; } = string.Empty;

    /// <summary>Base64 PBKDF2-SHA256 hash of the password.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Base64 per-user salt.</summary>
    public string Salt { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Admin;

    public DateTime Created { get; set; } = DateTime.Now;
}
