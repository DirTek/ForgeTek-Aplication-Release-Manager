using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISetupService
{
    Task<string> GenerateAsync(SetupBundle bundle, IProgress<SetupProgressInfo> progress, CancellationToken ct = default);
}
