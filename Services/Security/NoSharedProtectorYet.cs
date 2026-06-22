namespace ForgeTekUpdatePackager.Services.Security;

/// <summary>
/// Placeholder protector for the networked (shared SQL Server) deployment. A deployment-wide secret
/// encryption scheme is deliberately deferred, so this protector <b>refuses to protect</b> — it blocks
/// any attempt to persist a secret into the shared store, guaranteeing we never silently land plaintext
/// credentials in SQL Server before that scheme exists.
///
/// Reads pass through unchanged (already-decrypted/plaintext values load as-is); <see cref="IsProtected"/>
/// is always false so the storage layer treats values as plaintext. Replace this with the real shared-key
/// protector once the design lands. See the cross-server plan, §4.
/// </summary>
public sealed class NoSharedProtectorYet : ISecretProtector
{
    /// <summary>Thrown when a networked deployment tries to persist a secret before the shared-key
    /// scheme exists. Callers should surface this as "secret sharing isn't configured yet".</summary>
    public sealed class SecretSharingNotConfiguredException(string message) : InvalidOperationException(message);

    public string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        throw new SecretSharingNotConfiguredException(
            "Storing secrets in the shared database isn't supported yet. " +
            "A deployment-wide encryption scheme has not been configured, so credentials must stay " +
            "local for now. Configure local publish credentials on this machine instead.");
    }

    public string Unprotect(string? cipherText) => cipherText ?? string.Empty;

    public bool IsProtected(string? value) => false;
}
