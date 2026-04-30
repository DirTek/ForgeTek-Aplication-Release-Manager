using System.Security.Cryptography;
using System.Text;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Encrypts and decrypts strings using Windows DPAPI bound to the current user account.
/// Cipher text is stored as Base64. Empty/null values are passed through unchanged.
/// </summary>
public static class DpapiService
{
    // App-specific entropy so data encrypted here can't be decrypted by other apps
    // running as the same user.
    private static readonly byte[] Entropy =
        "ForgeTekUpdatePackager-v1"u8.ToArray();

    /// <summary>
    /// Encrypts <paramref name="plainText"/> and returns a Base64 cipher string.
    /// Returns <see cref="string.Empty"/> when the input is null or whitespace.
    /// </summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var bytes     = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a Base64 cipher string produced by <see cref="Protect"/>.
    /// Returns <see cref="string.Empty"/> when the input is null or whitespace.
    /// Returns <see cref="string.Empty"/> (and swallows the exception) if decryption fails
    /// — e.g. the data was not encrypted with DPAPI or was corrupted.
    /// </summary>
    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        try
        {
            var bytes     = Convert.FromBase64String(cipherText);
            var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="value"/> looks like a DPAPI-protected blob
    /// (valid Base64 that can be successfully decrypted by this service).
    /// Useful for migrating plain-text legacy values.
    /// </summary>
    public static bool IsProtected(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return true;
        }
        catch { return false; }
    }
}
