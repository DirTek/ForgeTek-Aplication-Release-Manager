using Microsoft.Win32;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// Stores the "protection enabled" marker in the Windows registry (HKCU), DPAPI-protected so it can't
/// be hand-forged. It lives in a different place than users.json, so deleting that file no longer
/// silently disables the login — startup sees the marker and locks instead.
/// (Honest limit: a user who also finds and clears this key can still reset protection.)
/// </summary>
public class ProtectionStateService : IProtectionStateService
{
    private const string KeyPath   = @"Software\ForgeTek\ReleaseManager";
    private const string ValueName = "Protection";
    private const string Token     = "enabled";

    public bool IsMarked
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (key?.GetValue(ValueName) is not string cipher || string.IsNullOrEmpty(cipher))
                    return false;
                return DpapiService.Unprotect(cipher) == Token;
            }
            catch { return false; }
        }
    }

    public void Mark()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(ValueName, DpapiService.Protect(Token), RegistryValueKind.String);
        }
        catch { /* registry unavailable — marker is best-effort */ }
    }

    public void Clear()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }
}
