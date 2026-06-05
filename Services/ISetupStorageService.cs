using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISetupStorageService
{
    IReadOnlyList<SetupBundle> GetAll();
    SetupBundle? GetById(string id);
    void Save(SetupBundle bundle);
    void Delete(string id);
}
