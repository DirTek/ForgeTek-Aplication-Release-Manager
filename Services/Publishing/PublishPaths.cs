namespace ForgeTekUpdatePackager.Services.Publishing;

/// <summary>Shared remote-path / download-URL construction for the path-based transports.</summary>
public static class PublishPaths
{
    /// <summary>
    /// Builds a server path "/{base}/{appKey}[/{version}]/{filename}". Strips an ftp(s):// scheme +
    /// host from <paramref name="basePath"/> so a full URL or a bare path both work.
    /// </summary>
    public static string ServerPath(string? basePath, string appKey, string? version, string filename)
    {
        var serverPath = basePath ?? string.Empty;
        if (serverPath.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            serverPath.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase) ||
            serverPath.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
        {
            var afterScheme = serverPath[(serverPath.IndexOf("//", StringComparison.Ordinal) + 2)..];
            var slashIdx = afterScheme.IndexOf('/');
            serverPath = slashIdx >= 0 ? afterScheme[slashIdx..] : "/";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(serverPath)) parts.Add(serverPath.TrimEnd('/'));
        parts.Add(appKey);
        if (version is not null) parts.Add(version);
        parts.Add(filename);
        return "/" + string.Join("/", parts).TrimStart('/');
    }

    /// <summary>Builds a public download URL "{base}/{appKey}/{version}/{filename}".</summary>
    public static string DownloadUrl(string? baseUrl, string appKey, string version, string filename)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return $"{appKey}/{version}/{filename}";

        var url = baseUrl.Trim().TrimEnd('/');
        if (url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[(url.IndexOf("//", StringComparison.Ordinal) + 2)..];

        return $"{url}/{appKey}/{version}/{filename}";
    }

    /// <summary>S3 object key "[prefix/]{appKey}[/{version}]/{filename}" (no leading slash).</summary>
    public static string S3Key(string? prefix, string appKey, string? version, string filename)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) parts.Add(prefix.Trim().Trim('/'));
        parts.Add(appKey);
        if (version is not null) parts.Add(version);
        parts.Add(filename);
        return string.Join("/", parts);
    }
}
