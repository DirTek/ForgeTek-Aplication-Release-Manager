using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Local user store (settings/users.json). Passwords are hashed with PBKDF2-SHA256 + per-user
/// salt — never stored or recoverable in plaintext. Usernames are matched case-insensitively.
/// </summary>
public class UserService : IUserService
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    private static readonly string UsersPath = Path.Combine(
        AppContext.BaseDirectory, "settings", "users.json");

    /// <summary>Absolute path of the user database — reused by backup/restore and the lockout flow.</summary>
    public static string UsersFilePath => UsersPath;

    /// <summary>Path of this file inside a backup ZIP.</summary>
    public const string UsersBackupEntry = "settings/users.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private List<AppUser> _users = [];

    public UserService() => Load();

    private void Load()
    {
        _users = [];
        if (!File.Exists(UsersPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<AppUser>>(File.ReadAllText(UsersPath), JsonOptions);
            if (list is not null) _users = list;
        }
        catch { _users = []; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UsersPath)!);
        File.WriteAllText(UsersPath, JsonSerializer.Serialize(_users, JsonOptions));
    }

    public bool HasAnyUsers => _users.Count > 0;

    public IReadOnlyList<AppUser> GetAll() => _users.AsReadOnly();

    public AppUser? GetByName(string username) =>
        _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

    public AppUser? Authenticate(string username, string password)
    {
        var user = GetByName(username);
        if (user is null) return null;
        return Verify(password, user.Salt, user.PasswordHash) ? user : null;
    }

    public AppUser Create(string username, string password, UserRole role)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.");
        if (GetByName(username) is not null)
            throw new InvalidOperationException($"A user named '{username}' already exists.");

        var (hash, salt) = Hash(password);
        var user = new AppUser { Username = username, PasswordHash = hash, Salt = salt, Role = role };
        _users.Add(user);
        Save();
        return user;
    }

    public void SetRole(string username, UserRole role)
    {
        if (GetByName(username) is { } u) { u.Role = role; Save(); }
    }

    public void SetPassword(string username, string password)
    {
        if (GetByName(username) is { } u)
        {
            (u.PasswordHash, u.Salt) = Hash(password);
            Save();
        }
    }

    public void Delete(string username)
    {
        var u = GetByName(username);
        if (u is not null) { _users.Remove(u); Save(); }
    }

    // ── PBKDF2 hashing ────────────────────────────────────────────────────
    private static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool Verify(string password, string saltB64, string hashB64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
