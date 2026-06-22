using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface ILicenseScanService
{
    /// <summary>Lists a project's NuGet dependencies and resolves each one's license from nuget.org.</summary>
    Task<LicenseReport> ScanAsync(string projectOrFolder, IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>Renders a plain-text "Third-Party Components" report.</summary>
    string BuildText(LicenseReport report, string subject);

    /// <summary>Renders an HTML "Third-Party Components" report.</summary>
    string BuildHtml(LicenseReport report, string subject);
}
