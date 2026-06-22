using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTekApplicationReleaseManager.Config;

/// <summary>Where the app keeps its data: a local SQLite file, or a shared SQL Server.</summary>
public enum StorageMode
{
    /// <summary>Single-machine: a local SQLite database. The default; works with no server.</summary>
    Standalone,

    /// <summary>Multi-operator: a shared SQL Server is the source of truth.</summary>
    Networked,
}

/// <summary>
/// Bootstrap configuration read from <c>settings/connection.json</c> (next to the EXE) <b>before</b> the
/// database exists, so it can't live in the DB itself. Decides which EF Core provider to use. The SQL
/// Server connection string is protected at rest with DPAPI <see cref="DataProtectionScope.LocalMachine"/>
/// (not CurrentUser) so any operator signed in on that machine can read it.
/// </summary>
public sealed class ConnectionConfig
{
    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "settings", "connection.json");

    private static readonly byte[] Entropy = "ForgeTekConnectionConfig-v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public StorageMode Mode { get; set; } = StorageMode.Standalone;

    /// <summary>SQL Server connection string used when <see cref="Mode"/> is Networked. Stored encrypted
    /// (DPAPI LocalMachine) on disk; this property holds the plaintext at runtime.</summary>
    [JsonIgnore]
    public string? SqlServerConnectionString { get; set; }

    /// <summary>The encrypted form persisted to disk. Round-trips via DPAPI LocalMachine.</summary>
    [JsonPropertyName("sqlServerConnectionStringProtected")]
    public string? SqlServerConnectionStringProtected { get; set; }

    /// <summary>SQLite file path used when <see cref="Mode"/> is Standalone. Empty = default
    /// <c>{RootFolder}/forgetek.db</c>, resolved by the caller that knows RootFolder.</summary>
    public string? SqlitePath { get; set; }

    public bool IsNetworked => Mode == StorageMode.Networked;

    public static ConnectionConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new ConnectionConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<ConnectionConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                      ?? new ConnectionConfig();
            cfg.SqlServerConnectionString = Unprotect(cfg.SqlServerConnectionStringProtected);
            return cfg;
        }
        catch { return new ConnectionConfig(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        SqlServerConnectionStringProtected = Protect(SqlServerConnectionString);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    // ── DPAPI LocalMachine (machine-scoped so any operator on the box can read it) ──
    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}
