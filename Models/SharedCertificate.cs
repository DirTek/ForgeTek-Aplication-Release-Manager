namespace ForgeTekUpdatePackager.Models;

/// <summary>
/// Metadata for a code-signing certificate shared through the networked database. The .pfx bytes live in
/// the store; the password is deliberately NOT shared (the private key stays protected by a password that
/// never lands in the DB), so operators enter it when registering on their own machine.
/// </summary>
public record SharedCertificate(
    string Id,
    string Subject,
    string FriendlyName,
    string Thumbprint,
    DateTime CreatedUtc,
    string? CreatedBy);
