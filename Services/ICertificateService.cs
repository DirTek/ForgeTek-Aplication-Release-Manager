namespace ForgeTekUpdatePackager.Services;

public interface ICertificateService
{
    Task<string> GenerateSelfSignedCertAsync(string subject, string friendlyName, string password,
        string outputFolder, bool keepInStore,
        IProgress<string> progress, CancellationToken ct = default);
}
