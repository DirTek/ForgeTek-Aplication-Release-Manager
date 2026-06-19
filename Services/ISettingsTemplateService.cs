using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISettingsTemplateService
{
    /// <summary>Built-in per-stack presets followed by the user's saved templates.</summary>
    IReadOnlyList<SettingsTemplate> GetAll();

    /// <summary>Adds or updates a user template (built-ins are ignored).</summary>
    void Save(SettingsTemplate template);

    /// <summary>Removes a user template by id (built-ins can't be deleted).</summary>
    void Delete(string id);
}
