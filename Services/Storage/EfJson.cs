using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ForgeTekUpdatePackager.Services.Security;

namespace ForgeTekUpdatePackager.Services.Storage;

/// <summary>Shared JSON options + secret-field helpers for the EF document-on-RDBMS services. Mirrors the
/// serialization the file-based services used so payloads are byte-compatible with the importer.</summary>
internal static class EfJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>Encrypts a secret field on a JSON object in place. When the active protector refuses
    /// (networked deployment, no shared-key scheme yet) the secret is <b>dropped</b> rather than stored
    /// plaintext — realizing "metadata-shared, credentials-not-yet-shared".</summary>
    public static void ProtectOrDrop(JsonObject obj, string key, ISecretProtector protector)
    {
        if (obj[key] is not JsonValue jv || !jv.TryGetValue<string>(out var val) || string.IsNullOrEmpty(val))
            return;
        try { obj[key] = protector.Protect(val); }
        catch (NoSharedProtectorYet.SecretSharingNotConfiguredException) { obj[key] = null; }
    }

    /// <summary>Decrypts a secret field on a JSON object in place (passes through legacy plaintext).</summary>
    public static void Decrypt(JsonObject obj, string key, ISecretProtector protector)
    {
        if (obj[key] is JsonValue jv && jv.TryGetValue<string>(out var val) && !string.IsNullOrEmpty(val))
            obj[key] = protector.IsProtected(val) ? protector.Unprotect(val) : val;
    }
}
