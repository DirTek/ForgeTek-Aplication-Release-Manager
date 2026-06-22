using System.IO;
using System.Text.Json;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services.Security;

namespace ForgeTekApplicationReleaseManager.Services;

public class SetupStorageService : ISetupStorageService
{
    private readonly string _setupsRoot;
    private readonly ILogService _log;
    private readonly ISecretProtector _protector;
    private List<SetupBundle> _bundles = [];
    private List<GeneratedSetupRecord> _history = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private string HistoryFilePath => Path.Combine(_setupsRoot, "history.json");

    public SetupStorageService(ISettingsService settings, ILogService log, ISecretProtector protector)
    {
        _setupsRoot = Path.Combine(settings.RootFolder, "setups");
        _log = log;
        _protector = protector;
        Load();
        LoadHistory();
    }

    private void Load()
    {
        _bundles = [];
        if (!Directory.Exists(_setupsRoot)) return;

        foreach (var file in Directory.GetFiles(_setupsRoot, "*.json"))
        {
            // history.json is the generation log, not a bundle — skip it here.
            if (string.Equals(Path.GetFileName(file), "history.json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var bundle = JsonSerializer.Deserialize<SetupBundle>(
                    File.ReadAllText(file), JsonOptions);
                if (bundle is not null)
                {
                    if (bundle.PublishProfile is not null) UnprotectProfile(bundle.PublishProfile);
                    _bundles.Add(bundle);
                }
            }
            catch (Exception ex)
            {
                _log.Write("SetupStorage", $"Failed to load {file}: {ex.Message}");
            }
        }
    }

    private void LoadHistory()
    {
        _history = [];
        if (!File.Exists(HistoryFilePath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<GeneratedSetupRecord>>(
                File.ReadAllText(HistoryFilePath), JsonOptions);
            if (list is not null) _history = list;
        }
        catch (Exception ex)
        {
            _log.Write("SetupStorage", $"Failed to load setup history: {ex.Message}");
        }
    }

    private void SaveHistory()
    {
        Directory.CreateDirectory(_setupsRoot);
        File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(_history, JsonOptions));
    }

    public IReadOnlyList<GeneratedSetupRecord> GetHistory() => _history.AsReadOnly();

    public void AddHistory(GeneratedSetupRecord record)
    {
        _history.Add(record);
        SaveHistory();
    }

    public void UpdateHistory(GeneratedSetupRecord record)
    {
        var idx = _history.FindIndex(r => r.Id == record.Id);
        if (idx >= 0) _history[idx] = record;
        else _history.Add(record);
        SaveHistory();
    }

    public void ClearHistory()
    {
        _history.Clear();
        SaveHistory();
    }

    public IReadOnlyList<SetupBundle> GetAll() => _bundles.AsReadOnly();

    public SetupBundle? GetById(string id) => _bundles.FirstOrDefault(b => b.Id == id);

    public void Save(SetupBundle bundle)
    {
        var path = FilePath(bundle.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // DPAPI-protect the publish profile's secrets on disk while keeping the in-memory copy usable.
        var plainProfile = bundle.PublishProfile;
        if (plainProfile is not null) bundle.PublishProfile = ProtectProfile(plainProfile);
        File.WriteAllText(path, JsonSerializer.Serialize(bundle, JsonOptions));
        bundle.PublishProfile = plainProfile;

        var idx = _bundles.FindIndex(b => b.Id == bundle.Id);
        if (idx >= 0) _bundles[idx] = bundle;
        else _bundles.Add(bundle);
    }

    // Mirrors SettingsService: encrypt the same FTP fields + SFTP/S3/GitHub secrets at rest.
    private PublishProfile ProtectProfile(PublishProfile p) => new()
    {
        PublishProvider = p.PublishProvider,
        FtpHost = _protector.Protect(p.FtpHost), FtpPort = p.FtpPort,
        FtpUsername = _protector.Protect(p.FtpUsername), FtpPassword = _protector.Protect(p.FtpPassword),
        FtpRemotePath = _protector.Protect(p.FtpRemotePath), BaseDownloadUrl = _protector.Protect(p.BaseDownloadUrl),
        SftpHost = p.SftpHost, SftpPort = p.SftpPort, SftpUsername = p.SftpUsername,
        SftpPassword = _protector.Protect(p.SftpPassword), SftpRemotePath = p.SftpRemotePath,
        SftpBaseDownloadUrl = p.SftpBaseDownloadUrl,
        S3Endpoint = p.S3Endpoint, S3Region = p.S3Region, S3Bucket = p.S3Bucket, S3AccessKey = p.S3AccessKey,
        S3SecretKey = _protector.Protect(p.S3SecretKey), S3Prefix = p.S3Prefix, S3PublicBaseUrl = p.S3PublicBaseUrl,
        GitHubRepo = p.GitHubRepo, GitHubToken = _protector.Protect(p.GitHubToken),
        GitHubReleaseTag = p.GitHubReleaseTag, GitHubCatalogTag = p.GitHubCatalogTag,
    };

    private void UnprotectProfile(PublishProfile p)
    {
        p.FtpHost = DecryptOrPassthrough(p.FtpHost);
        p.FtpUsername = DecryptOrPassthrough(p.FtpUsername);
        p.FtpPassword = DecryptOrPassthrough(p.FtpPassword);
        p.FtpRemotePath = DecryptOrPassthrough(p.FtpRemotePath);
        p.BaseDownloadUrl = DecryptOrPassthrough(p.BaseDownloadUrl);
        p.SftpPassword = DecryptOrPassthrough(p.SftpPassword);
        p.S3SecretKey = DecryptOrPassthrough(p.S3SecretKey);
        p.GitHubToken = DecryptOrPassthrough(p.GitHubToken);
    }

    private string? DecryptOrPassthrough(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return _protector.IsProtected(value) ? _protector.Unprotect(value) : value;
    }

    public void Delete(string id)
    {
        var bundle = _bundles.FirstOrDefault(b => b.Id == id);
        if (bundle is null) return;

        var path = FilePath(bundle.Name);
        if (File.Exists(path))
            File.Delete(path);

        _bundles.Remove(bundle);
    }

    private string FilePath(string name)
    {
        var sanitized = StorageService.Sanitize(name);
        return Path.Combine(_setupsRoot, $"{sanitized}.json");
    }
}
