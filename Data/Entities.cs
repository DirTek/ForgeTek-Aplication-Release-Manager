using System.ComponentModel.DataAnnotations;

namespace ForgeTekApplicationReleaseManager.Data;

/// <summary>
/// Common shape for the document-on-RDBMS rows: a primary key, a JSON <see cref="Payload"/> holding the
/// serialized domain model, audit stamps, and a provider-neutral concurrency token (<see cref="RowVersion"/>)
/// reassigned on every save (see <see cref="ForgeTekDbContext.SaveChanges()"/>). Using a string token rather
/// than SQL Server <c>rowversion</c> keeps one migration set working on both SQLite and SQL Server.
/// </summary>
public interface IConcurrencyStamped
{
    string RowVersion { get; set; }
}

/// <summary>One app + its full version history (serialized <c>AppEntry</c>).</summary>
public class AppRow : IConcurrencyStamped
{
    [Key] public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
    [ConcurrencyCheck] public string RowVersion { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>Per-app settings (serialized <c>AppSettings</c>), keyed by app name to mirror today's files.</summary>
public class AppSettingsRow : IConcurrencyStamped
{
    [Key] public string AppName { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    [ConcurrencyCheck] public string RowVersion { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>One setup bundle (serialized <c>SetupBundle</c>).</summary>
public class SetupBundleRow : IConcurrencyStamped
{
    [Key] public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    [ConcurrencyCheck] public string RowVersion { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>One "Past Bundles" generation record (serialized <c>GeneratedSetupRecord</c>).</summary>
public class SetupHistoryRow : IConcurrencyStamped
{
    [Key] public string Id { get; set; } = string.Empty;
    public string BundleId { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; }
    public string Payload { get; set; } = string.Empty;
    [ConcurrencyCheck] public string RowVersion { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>One app user (serialized <c>AppUser</c>; PBKDF2 hash inside the payload, never plaintext).</summary>
public class UserRow : IConcurrencyStamped
{
    /// <summary>Lower-cased username (matches the case-insensitive lookup today).</summary>
    [Key] public string UsernameKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    [ConcurrencyCheck] public string RowVersion { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>The single global-settings row (serialized <c>GlobalSettings</c>) + the shared protection flag.</summary>
public class GlobalSettingsRow : IConcurrencyStamped
{
    /// <summary>Always <see cref="SingletonId"/> — there is exactly one global-settings row.</summary>
    [Key] public int Id { get; set; } = SingletonId;
    public const int SingletonId = 1;
    public string Payload { get; set; } = string.Empty;
    /// <summary>Shared "access protection enabled" marker (replaces the per-machine registry key).</summary>
    public bool ProtectionEnabled { get; set; }
    [ConcurrencyCheck] public string RowVersion { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>
/// A content-addressed source-file blob (networked mode only). Keyed by the file's SHA-256 (the same
/// lowercase hex checksum on <c>FileRecord.Checksum</c>), so identical files across versions/apps are
/// stored once. Lets any operator (re)package a version without the original local source folder. This is
/// a binary store, not the document-on-RDBMS JSON <c>Payload</c> pattern.
/// </summary>
public class FileBlobRow
{
    /// <summary>Lowercase hex SHA-256 of the original file content.</summary>
    [Key] public string Sha256 { get; set; } = string.Empty;
    /// <summary>Original (uncompressed) size in bytes.</summary>
    public long Length { get; set; }
    /// <summary>Whether <see cref="Content"/> is GZip-compressed (false = stored raw, e.g. already-compressed assets).</summary>
    public bool Compressed { get; set; }
    /// <summary>The file bytes (GZip-compressed when <see cref="Compressed"/>). BLOB / varbinary(max).</summary>
    public byte[] Content { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A code-signing certificate shared through the networked store. Holds the password-protected .pfx bytes
/// and display metadata — but never the password (the private key stays protected by a secret that isn't in
/// the DB; operators supply it when registering locally).
/// </summary>
public class CertificateRow
{
    [Key] public string Id { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string Thumbprint { get; set; } = string.Empty;
    /// <summary>The password-protected PKCS#12 (.pfx) bytes. BLOB / varbinary(max).</summary>
    public byte[] Pfx { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

/// <summary>One immutable approval/reject/note on a release target (serialized <c>ReleaseApproval</c>).</summary>
public class ApprovalRow
{
    [Key] public string Id { get; set; } = string.Empty;
    /// <summary>Stable key for the target being voted on (e.g. <c>app:{appId}:{version}</c> or
    /// <c>setup:{bundleId}:{recordId}</c>), so all votes on one release group together.</summary>
    public string TargetKey { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string Payload { get; set; } = string.Empty;
}
