using System.IO;
using System.Security.Cryptography;

namespace ForgeTekUpdatePackager.Services;

/// <summary>Small helpers for hashing files (e.g. the installer SHA256 winget requires).</summary>
public static class HashUtil
{
    /// <summary>Streams a file through SHA256 and returns the hex digest. Uppercase by default
    /// (winget convention); lowercase elsewhere in the app uses <paramref name="upper"/> = false.</summary>
    public static string Sha256File(string path, bool upper = true)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        var hex = Convert.ToHexString(hash);            // uppercase
        return upper ? hex : hex.ToLowerInvariant();
    }
}
