using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface ISetupService
{
    Task<string> GenerateAsync(SetupBundle bundle, IProgress<SetupProgressInfo> progress, CancellationToken ct = default);
}
