using System.Security.Cryptography.X509Certificates;

namespace ForgeTekApplicationReleaseManager.Helpers;

public record StoreCertInfo(string Thumbprint, string Subject, string Issuer, DateTime NotAfter)
{
    public string DisplayName => $"{Subject}  (exp: {NotAfter:d})";
    public bool IsExpired => NotAfter < DateTime.Now;

    public static StoreCertInfo FromX509(X509Certificate2 cert) => new(
        cert.Thumbprint,
        cert.Subject,
        cert.Issuer,
        cert.NotAfter);
}
