using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISettingsService
{
    GlobalSettings Global { get; }
    string RootFolder { get; }
    void SaveGlobal();
    AppSettings LoadAppSettings(string appName);
    void SaveAppSettings(string appName, AppSettings settings);
    string GetDefaultOutputBase(string appName);
    string GetVersionOutputPath(string appName, string version, AppSettings? appSettings = null);
}
