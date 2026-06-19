using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISetupStorageService
{
    IReadOnlyList<SetupBundle> GetAll();
    SetupBundle? GetById(string id);
    void Save(SetupBundle bundle);
    void Delete(string id);

    // ── Generation history ("Past Bundles") ──────────────────────────────
    IReadOnlyList<GeneratedSetupRecord> GetHistory();
    void AddHistory(GeneratedSetupRecord record);
    /// <summary>Persists changes to an existing history record (e.g. after publishing).</summary>
    void UpdateHistory(GeneratedSetupRecord record);
    void ClearHistory();
}
