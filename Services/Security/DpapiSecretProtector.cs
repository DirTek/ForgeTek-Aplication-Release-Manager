namespace ForgeTekApplicationReleaseManager.Services.Security;

/// <summary>
/// Default protector for local/standalone installs — wraps Windows DPAPI bound to the current user
/// (<see cref="DpapiService"/>). Behavior is identical to the original static usage, so solo users
/// see no change. Not usable across machines/users — see <see cref="NoSharedProtectorYet"/>.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string? plainText)   => DpapiService.Protect(plainText);
    public string Unprotect(string? cipherText) => DpapiService.Unprotect(cipherText);
    public bool   IsProtected(string? value)    => DpapiService.IsProtected(value);
}
