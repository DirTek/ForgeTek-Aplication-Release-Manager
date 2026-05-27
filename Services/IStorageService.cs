using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface IStorageService
{
    IReadOnlyList<AppEntry> GetAll();
    AppEntry? GetById(string id);
    void Add(AppEntry app);
    void Update(AppEntry app);
    void Delete(string id);
}
