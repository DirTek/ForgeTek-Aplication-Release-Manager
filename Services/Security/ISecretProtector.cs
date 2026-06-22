namespace ForgeTekApplicationReleaseManager.Services.Security;

/// <summary>
/// Encrypts/decrypts secret strings (passwords, tokens, keys) for storage at rest. Abstracts the
/// concrete scheme so the storage services don't care whether secrets are protected with Windows
/// DPAPI (local installs) or a future deployment-wide key (shared SQL Server). Mirrors the surface
/// of the legacy <see cref="DpapiService"/> so call sites swap one-for-one.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Protects a plaintext secret, returning an opaque (typically Base64) cipher string.
    /// Empty/null input returns <see cref="string.Empty"/>.</summary>
    string Protect(string? plainText);

    /// <summary>Reverses <see cref="Protect"/>. Empty/null input returns <see cref="string.Empty"/>.</summary>
    string Unprotect(string? cipherText);

    /// <summary>True when <paramref name="value"/> is a cipher string this protector can decrypt
    /// (used to migrate legacy plaintext values in place).</summary>
    bool IsProtected(string? value);
}
