namespace ForgeTekUpdatePackager.Services;

public interface ICertificateService
{
    Task<string> GenerateSelfSignedCertAsync(string subject, string friendlyName, string password,
        string outputFolder, bool keepInStore,
        IProgress<string> progress, CancellationToken ct = default);

    /// <summary>Reads the SHA-1 thumbprint from a password-protected .pfx (throws on a wrong password).</summary>
    string ReadThumbprint(byte[] pfx, string password);

    /// <summary>Imports a .pfx (with its private key) into the current user's personal certificate store,
    /// so signing can use it locally by thumbprint. Throws on a wrong password.</summary>
    void ImportToUserStore(byte[] pfx, string password);
}
